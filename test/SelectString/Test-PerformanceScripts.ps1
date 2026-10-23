# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Checks the Select-String performance module and its delegating scripts.
.DESCRIPTION
Runs completion/help/reimport checks, small real benchmark workers, process
failure/timeout checks and capture orchestration with a fake recorder. Never
starts WPR. Test artifacts are retained under tmp/SelectString for inspection.
.PARAMETER PwshPath
PowerShell executable used by the small measurement smoke tests.
.EXAMPLE
./test/SelectString/Test-PerformanceScripts.ps1 -PwshPath ./tmp/SelectString/candidate/pwsh.exe
#>
[CmdletBinding()]
param([string] $PwshPath = (Get-Process -Id $PID).Path)

$ErrorActionPreference = 'Stop'
$PwshPath = (Resolve-Path $PwshPath).ProviderPath
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$source = Join-Path $repo 'test/perf/SelectString'
$modulePath = Join-Path $source 'SelectString.Performance.psm1'
$artifacts = Join-Path $repo ('tmp/SelectString/module-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $artifacts -Force
function Assert([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}
function Assert-Throws([scriptblock] $Action, [string] $Message) {
    $caught = $false
    try { & $Action } catch { $caught = $true }
    Assert $caught $Message
}

& (Join-Path $source 'Initialize-Performance.ps1')
$null = Test-ModuleManifest (Join-Path $source 'SelectString.Performance.psd1')
foreach ($script in 'Measure-SelectString.ps1','New-EtwCapture.ps1','New-LogFixture.ps1','../../SelectString/Compare-SelectString.ps1') {
    $help = Get-Help (Join-Path $source $script) -Full
    Assert ($help.parameters.parameter.Count -gt 0) "Missing forwarded help: $script"
    $command = Get-Command (Join-Path $source $script)
    Assert ($command.Parameters.Count -gt 0) "Missing dynamic parameters: $script"
}
$module = Import-Module $modulePath -Force -PassThru
# A fake repository keeps candidate discovery and recorder tests independent of local builds.
$fake = Join-Path $artifacts 'repository'
$fakeModuleDirectory = Join-Path $fake 'test/perf/SelectString'
$buildRoot = Join-Path $fake 'tmp/SelectString'
$null = New-Item -ItemType Directory -Path $fakeModuleDirectory -Force
Copy-Item -LiteralPath $modulePath -Destination $fakeModuleDirectory
foreach ($file in 'SelectString.Performance.psd1','Initialize-Performance.ps1','New-EtwCapture.ps1') {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $fakeModuleDirectory
}
Copy-Item -LiteralPath (Join-Path $source 'SelectString.wprp') -Destination $fakeModuleDirectory
foreach ($name in 'baseline','first-extraction','experiment','older','newer') {
    $directory = Join-Path $buildRoot $name
    $null = New-Item -ItemType Directory -Path $directory -Force
    Set-Content (Join-Path $directory 'pwsh.exe') ''
    Set-Content (Join-Path $directory 'Microsoft.PowerShell.Commands.Utility.dll') ''
    (Get-Item (Join-Path $directory 'Microsoft.PowerShell.Commands.Utility.dll')).LastWriteTimeUtc = [datetime]'2020-01-01'
}
(Get-Item (Join-Path $buildRoot 'newer/Microsoft.PowerShell.Commands.Utility.dll')).LastWriteTimeUtc = [datetime]'2021-01-01'
$fixture = Join-Path $buildRoot 'fixture with spaces'
$null = New-Item -ItemType Directory -Path $fixture -Force
Set-Content (Join-Path $fixture 'application.log') ''
Set-Content (Join-Path $fixture 'manifest.json') '{}'

$fakeModulePath = Join-Path $fakeModuleDirectory 'SelectString.Performance.psm1'
$fakeModule = Import-Module $fakeModulePath -Force -PassThru
$selection = & $fakeModule {
    $catalog = [BenchmarkBuildCatalog]::new([BenchmarkPaths]::BuildRoot)
    [pscustomobject]@{ Names = @($catalog.Candidates().Name); Latest = $catalog.Newest().Name }
}
Assert ($selection.Latest -eq 'newer') 'Newest assembly selection failed'
Assert (($selection.Names | Sort-Object) -join ',' -eq 'newer,older') 'Excluded build discovered'
$line = "& '$(Join-Path $fakeModuleDirectory 'New-EtwCapture.ps1')' -CandidateDirectory ne"
$completion = [System.Management.Automation.CommandCompletion]::CompleteInput($line, $line.Length, $null)
Assert ($completion.CompletionMatches.CompletionText -contains 'newer') 'Entry-script completion failed'
$fakeModule = Get-Module SelectString.Performance
$command = Get-Command Invoke-SelectStringCapture
$attribute = $command.Parameters.CandidateDirectory.Attributes | Where-Object { $_ -is [System.Management.Automation.IArgumentCompleterFactory] }
Assert ($null -ne $attribute) 'Factory attribute missing'
$completion = @($attribute.Create().CompleteArgument('', '', 'ne', $null, @{}))
Assert ($completion.Count -eq 1 -and $completion[0].CompletionText -eq 'newer') 'Class completer prefix failed'
$attribute = $command.Parameters.LogFixture.Attributes | Where-Object { $_ -is [System.Management.Automation.IArgumentCompleterFactory] }
$completion = @($attribute.Create().CompleteArgument('', '', 'fixture', $null, @{}))
Assert ($completion[0].CompletionText -eq ("'" + $fixture + "'")) 'Fixture completion quoting failed'

# Verify that changed class definitions, not just functions, are picked up by Force.
$text = Get-Content $fakeModulePath -Raw
$text.Replace('class BenchmarkPaths {', "class BenchmarkPaths {`n    static [int] `$ReloadCheck = 1") | Set-Content $fakeModulePath
$fakeModule = Import-Module $fakeModulePath -Force -PassThru
Assert ((& $fakeModule { [BenchmarkPaths]::ReloadCheck }) -eq 1) 'Initial class definition missing'
$text.Replace('class BenchmarkPaths {', "class BenchmarkPaths {`n    static [int] `$ReloadCheck = 2") | Set-Content $fakeModulePath
$fakeModule = Import-Module $fakeModulePath -Force -PassThru
Assert ((& $fakeModule { [BenchmarkPaths]::ReloadCheck }) -eq 2) 'Changed class definition was not reimported'

& $fakeModule {
    $script:Calls = [Collections.Generic.List[string]]::new()
    $script:RecorderMode = 'Success'
    function script:wpr.exe {
        $Operation = $args[0]
        $script:Calls.Add($Operation)
        $script:LASTEXITCODE = 0
        if ($Operation -eq '-start' -and $script:RecorderMode -eq 'StartFailure') { $script:LASTEXITCODE = 1 }
        if ($Operation -eq '-stop' -and $script:RecorderMode -eq 'StopFailure') { $script:LASTEXITCODE = 1 }
        if ($Operation -eq '-stop' -and $script:RecorderMode -eq 'Loss') { 'This trace has dropped 123 events.' }
    }
    function script:Invoke-SelectStringMeasurement {
        $path = $args[[Array]::IndexOf($args, '-Artifacts') + 1]
        $null = New-Item -ItemType Directory -Path $path -Force
        $status = if ($script:RecorderMode -eq 'WorkerFailure') { 'Exit 1' } else { 'Measured' }
        @{ Status = $status } | ConvertTo-Json | Set-Content (Join-Path $path 'results.json')
    }
}
foreach ($mode in 'Success','StartFailure','WorkerFailure','StopFailure','Loss') {
    & $fakeModule { param($value) $script:Calls.Clear(); $script:RecorderMode = $value } $mode
    $failed = $false
    try { & $fakeModule { Invoke-SelectStringCapture -CandidateDirectory newer -Iterations 1 -WarmupIterations 1 } }
    catch { $failed = $true }
    $calls = (& $fakeModule { $script:Calls.ToArray() }) -join ','
    if ($mode -eq 'Success') {
        Assert (!$failed -and $calls -eq '-start,-stop,-start,-stop') 'Two-build capture delegation failed'
    } elseif ($mode -eq 'StartFailure') {
        Assert ($failed -and $calls -eq '-start') 'Start failure must not stop another session'
    } else {
        Assert ($failed -and $calls -eq '-start,-stop') "Cleanup/abort failed for $mode"
    }
}

# Verify batch ordering, isolated outputs, one shared fixture resolution, and deduplication.
& $fakeModule {
    $script:Calls.Clear()
    $script:RecorderMode = 'Success'
    $script:FixtureResolutions = 0
    $script:Measurements = [Collections.Generic.List[object]]::new()
    function script:Resolve-SelectStringLogFixture {
        param($Directory)
        $script:FixtureResolutions++
        return 'mock-fixture'
    }
    function script:Invoke-SelectStringMeasurement {
        $path = $args[[Array]::IndexOf($args, '-Artifacts') + 1]
        $scenario = $args[[Array]::IndexOf($args, '-Scenario') + 1]
        $revision = $args[[Array]::IndexOf($args, '-Revision') + 1]
        $script:Measurements.Add([pscustomobject]@{ Scenario = $scenario; Revision = $revision; Path = $path })
        $null = New-Item -ItemType Directory -Path $path -Force
        @{ Status = 'Measured' } | ConvertTo-Json | Set-Content (Join-Path $path 'results.json')
    }
    Invoke-SelectStringCapture -CandidateDirectory newer -Scenario LogSparse,LogAllMatches,LogSparse -Iterations 1 -WarmupIterations 1
}
$batch = @(& $fakeModule { $script:Measurements.ToArray() })
Assert (($batch | ForEach-Object { "$($_.Scenario)/$($_.Revision)" }) -join ',' -eq 'LogSparse/shipping,LogSparse/candidate,LogAllMatches/shipping,LogAllMatches/candidate') 'Batch scenario/build order failed'
Assert (@($batch.Path | Select-Object -Unique).Count -eq 4) 'Batch output paths collide'
Assert ((& $fakeModule { $script:Calls.Count }) -eq 8) 'Batch must create four separate recordings'
Assert ((& $fakeModule { $script:FixtureResolutions }) -eq 1) 'Batch should resolve its shared fixture once'
Assert ((Get-Command (Join-Path $source 'New-EtwCapture.ps1')).Parameters.Scenario.ParameterType -eq [string[]]) 'Wrapper Scenario must accept arrays'

$module = Import-Module $modulePath -Force -PassThru
foreach ($case in @(@{ Code = 'exit 7'; Status = 'Exit 7' }, @{ Code = 'Start-Sleep -Seconds 10'; Status = 'Timeout' })) {
    $result = & $module {
        param($executable, $code, $output)
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($arg in '-NoProfile','-Command',$code) { $start.ArgumentList.Add($arg) }
        [BenchmarkWorker]::Run($start, 1, 0, 'Test', $output)
    } $PwshPath $case.Code (Join-Path $artifacts 'worker.json')
    Assert ($result.Status -eq $case.Status) "Worker status incorrect: $($result.Status)"
}

$fixture = Join-Path $artifacts 'fixture'
& (Join-Path $source 'New-LogFixture.ps1') -Directory $fixture -SizeMiB 1
$output = Join-Path $artifacts 'measurements'
& (Join-Path $source 'Measure-SelectString.ps1') -PwshPath $PwshPath -Revision test `
    -Scenario String,DenseContext,NoMatchContext,LogAllMatches,LogContext `
    -LogFixture $fixture -LineCount 100 -Iterations 1 -WarmupIterations 1 -Artifacts $output
$results = @(Get-Content (Join-Path $output 'results.json') -Raw | ConvertFrom-Json)
Assert ($results.Count -eq 5 -and @($results | Where-Object Status -ne Measured).Count -eq 0) 'Measurement delegation failed'
Assert (($results | Where-Object Scenario -eq DenseContext).ResultCount -eq 100) 'Dense count incorrect'
Assert (($results | Where-Object Scenario -eq NoMatchContext).ResultCount -eq 0) 'No-match count incorrect'
Assert ($results[0].HostSupportsVirtualTerminal -is [bool]) 'Terminal capability not recorded'
& (Join-Path $source 'Measure-SelectString.ps1') -PwshPath $PwshPath -Revision test `
    -Scenario PipelineCaptures,PipelineStop,FileStop -RecordCount 37 -MatchEvery 4 `
    -LineCount 100 -Iterations 1 -WarmupIterations 1 -LatencyIterations 3 -Artifacts (Join-Path $artifacts 'pipelines')
$pipelineResults = @(Get-Content (Join-Path $artifacts 'pipelines/results.json') -Raw | ConvertFrom-Json)
Assert (@($pipelineResults | Where-Object Status -ne Measured).Count -eq 0) 'Pipeline measurement failed'
Assert ($pipelineResults.Count -eq 3) 'Missing pipeline cases'
Assert ($pipelineResults[0].ResultCount -eq 10 -and $pipelineResults[0].ValidatedResults -eq 10) 'Pipeline density/count incorrect'
Assert ($pipelineResults[0].PipelineRecordCount -eq 37 -and $pipelineResults[0].PipelineMatchEvery -eq 4) 'Pipeline metadata/worker forwarding incorrect'
Assert ($pipelineResults[1].ResultCount -eq 1 -and $pipelineResults[2].ResultCount -eq 1) 'Downstream termination incorrect'
foreach ($pipelineResult in $pipelineResults) {
    Assert ($pipelineResult.LatencyIterations -eq 3 -and $pipelineResult.FirstResultSamplesMs.Count -eq 3) 'Latency samples missing'
    $latencies = @($pipelineResult.FirstResultSamplesMs | Sort-Object)
    Assert ($pipelineResult.FirstResultMs -eq $latencies[1]) 'Latency median incorrect'
}
$log = $results | Where-Object Scenario -eq LogAllMatches
foreach ($inputKind in 'Mixed','Absent') {
    $patternOutput = Join-Path $artifacts "patterns-$inputKind"
    & (Join-Path $source 'Measure-SelectString.ps1') -PwshPath $PwshPath -Revision test `
        -Scenario PatternFile,PatternRecords -PatternFamily SqlDuration -InputKind $inputKind `
        -RecordCount 17 -Culture tr-TR -CaseSensitive -LineCount 16 `
        -Iterations 1 -WarmupIterations 1 -Artifacts $patternOutput
    $patternResults = @(Get-Content (Join-Path $patternOutput 'results.json') -Raw | ConvertFrom-Json)
    Assert ($patternResults.Count -eq 2) 'Missing pattern scenarios'
    foreach ($patternResult in $patternResults) {
        Assert ($patternResult.Status -eq 'Measured') 'Pattern measurement failed'
        $expectedCount = $inputKind -eq 'Mixed' ? 5 : 0
        Assert ($patternResult.ResultCount -eq $expectedCount -and $patternResult.ValidatedResults -eq $expectedCount) 'Pattern validation/count incorrect'
        Assert ($patternResult.PatternFamily -eq 'SqlDuration' -and $patternResult.PatternInput -eq $inputKind -and $patternResult.PatternRecordCount -eq 17) 'Pattern parameters not forwarded'
        Assert ($patternResult.PatternCulture -eq 'tr-TR' -and $patternResult.PatternCaseSensitive) 'Pattern options not forwarded'
        if ($expectedCount -eq 0) { Assert ($null -eq $patternResult.FirstResultMs) 'Absent pattern must not have first-result latency' }
    }
}
Assert ($log.ValidatedResults -eq $log.ResultCount -and $log.ValidatedMatches -gt $log.ResultCount) 'Capture validation missing'
& (Join-Path $source 'Measure-SelectString.ps1') -PwshPath $PwshPath -Revision test `
    -Scenario LogContext -LogFixture $fixture -Iterations 1 -WarmupIterations 1 `
    -TraceMarkers -SkipCounting -Artifacts (Join-Path $artifacts 'markers')
$result = Get-Content (Join-Path $artifacts 'markers/results.json') -Raw | ConvertFrom-Json
Assert ($result.Status -eq 'Measured' -and $result.TraceMarkers -and !$result.CountingMeasured -and $null -eq $result.ResultCount -and $result.ValidatedResults -gt 0) 'Marker/SkipCounting semantics changed'
Assert ($result.LatencyIterations -eq 0 -and $result.FirstResultSamplesMs.Count -eq 0 -and $null -eq $result.FirstResultMs) 'Skipped latency should have no samples'
Add-Content (Join-Path $fixture 'application.log') 'corruption'
Assert-Throws { & (Join-Path $source 'Measure-SelectString.ps1') -PwshPath $PwshPath -Revision test -Scenario LogSparse -LogFixture $fixture -Artifacts (Join-Path $artifacts 'invalid') } 'Invalid fixture accepted'

# Exercise comparison workers through the wrapper and verify the function export.
$comparison = & (Join-Path $repo 'test/SelectString/Compare-SelectString.ps1') -Baseline $PwshPath -Candidate $PwshPath -Artifacts (Join-Path $artifacts 'comparison')
Assert ($comparison -match 'Passed 146 baseline comparisons') 'Comparison wrapper/worker delegation failed'
Assert ($null -ne (Get-Command Invoke-SelectStringComparison -Module SelectString.Performance)) 'Comparison implementation is not exported'
Write-Host "Performance script checks passed. Artifacts: $artifacts"
