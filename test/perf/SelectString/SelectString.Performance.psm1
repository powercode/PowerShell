# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

using namespace System.Management.Automation
using namespace System.Management.Automation.Language
using namespace System.Collections.Generic

# Module-local types are accessed through the wrapper completion delegate. Avoid
# `using module` in the entry scripts, which would pin parse-time class metadata.
class BenchmarkPaths {
    static [string] $BuildRoot
}

# Shared candidate discovery used by selection and completion.
class BenchmarkBuildCatalog {
    [string] $Root
    BenchmarkBuildCatalog([string] $root) { $this.Root = $root }

    [System.IO.DirectoryInfo[]] Candidates() {
        return @(Get-ChildItem -LiteralPath $this.Root -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -notin 'baseline','first-extraction','experiment','shipping' -and
                $_.Name -match '^[a-zA-Z0-9][a-zA-Z0-9_.-]*$' -and
                (Test-Path -LiteralPath (Join-Path $_.FullName 'pwsh.exe'))
            })
    }

    static [datetime] Timestamp([System.IO.DirectoryInfo] $directory) {
        $assembly = Get-Item -LiteralPath (Join-Path $directory.FullName 'Microsoft.PowerShell.Commands.Utility.dll') -ErrorAction SilentlyContinue
        if ($assembly) { return $assembly.LastWriteTimeUtc }
        return (Get-Item -LiteralPath (Join-Path $directory.FullName 'pwsh.exe')).LastWriteTimeUtc
    }

    [System.IO.DirectoryInfo] Newest() {
        $latest = $this.Candidates() | Sort-Object @{
            Expression = { [BenchmarkBuildCatalog]::Timestamp($_) }; Descending = $true
        }, Name | Select-Object -First 1
        if (!$latest) { throw 'No candidate build found under tmp/SelectString. Build PowerShell first.' }
        return $latest
    }
}

# A single completer handles build names and shared fixture paths. The factory
# attribute supplies the mode; completion never creates files or starts workers.
class BenchmarkDirectoryCompleter : IArgumentCompleter {
    [string] $Kind
    [string] $Root
    BenchmarkDirectoryCompleter([string] $kind, [string] $root) {
        $this.Kind = $kind
        $this.Root = $root
    }

    [IEnumerable[CompletionResult]] CompleteArgument(
        [string] $commandName, [string] $parameterName, [string] $wordToComplete,
        [CommandAst] $commandAst, [System.Collections.IDictionary] $fakeBoundParameters) {
        $results = [List[CompletionResult]]::new()
        $prefix = $wordToComplete.Trim("'", '"')
        if ($this.Kind -eq 'Candidate') {
            $directories = [BenchmarkBuildCatalog]::new($this.Root).Candidates()
        } else {
            $directories = @(Get-ChildItem -LiteralPath $this.Root -Directory -ErrorAction SilentlyContinue |
                Where-Object {
                    (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) -and
                    (Test-Path -LiteralPath (Join-Path $_.FullName 'application.log'))
                })
        }
        foreach ($directory in ($directories | Sort-Object Name)) {
            if ($directory.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                $directory.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $value = $directory.Name
                if ($this.Kind -eq 'Fixture') { $value = "'" + $directory.FullName.Replace("'", "''") + "'" }
                $results.Add([CompletionResult]::new($value, $directory.Name, 'ParameterValue', $directory.FullName))
            }
        }
        return $results
    }
}

class BenchmarkDirectoryCompletionAttribute : ArgumentCompleterAttribute, IArgumentCompleterFactory {
    [string] $Kind
    BenchmarkDirectoryCompletionAttribute([string] $kind) { $this.Kind = $kind }
    [IArgumentCompleter] Create() {
        return [BenchmarkDirectoryCompleter]::new($this.Kind, [BenchmarkPaths]::BuildRoot)
    }
}

# Owns child-process lifetime and converts timeout/nonzero exit into result rows.
class BenchmarkWorker {
    static [object] Run([System.Diagnostics.ProcessStartInfo] $start, [int] $timeoutSeconds,
        [long] $affinity, [string] $scenario, [string] $output) {
        $process = [System.Diagnostics.Process]::Start($start)
        try {
            if ($affinity) { $process.ProcessorAffinity = [IntPtr]$affinity }
            if (!$process.WaitForExit($timeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                return [pscustomobject]@{ Scenario = $scenario; Status = 'Timeout' }
            }
            if ($process.ExitCode) { return [pscustomobject]@{ Scenario = $scenario; Status = "Exit $($process.ExitCode)" } }
            return (Get-Content -LiteralPath $output -Raw | ConvertFrom-Json)
        } finally { $process.Dispose() }
    }
}

[BenchmarkPaths]::BuildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../tmp/SelectString'))

function New-SelectStringScriptParameters {
    <#
    .SYNOPSIS
    Supplies a thin entry script with the implementation's parameter metadata.
    .DESCRIPTION
    Copies types and attributes, including class-based completer factories, from
    a module function. Common parameters are supplied by the wrapper itself.
    Defaults are applied by the implementation when a parameter was not bound.
    .PARAMETER CommandName
    Module implementation to delegate to. Only the public entry points are accepted.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Invoke-SelectStringMeasurement','Invoke-SelectStringCapture','New-SelectStringLogFixture','Invoke-SelectStringComparison')]
        [string] $CommandName
    )
    $parameters = [RuntimeDefinedParameterDictionary]::new()
    $command = Get-Command -Name $CommandName -Module SelectString.Performance
    foreach ($parameter in $command.Parameters.Values) {
        if ($parameter.Name -in [PSCmdlet]::CommonParameters -or $parameter.Name -in [PSCmdlet]::OptionalCommonParameters) { continue }
        $attributes = [System.Collections.ObjectModel.Collection[Attribute]]::new()
        foreach ($attribute in $parameter.Attributes) { $attributes.Add($attribute) }
        $parameters.Add($parameter.Name, [RuntimeDefinedParameter]::new($parameter.Name, $parameter.ParameterType, $attributes))
    }
    return $parameters
}

function Resolve-SelectStringLogFixture {
    <#
    .SYNOPSIS
    Resolves and verifies a shared fixture before measurement or recording.
    .PARAMETER Directory
    Explicit fixture directory, or empty to reuse/create the default 300 MiB log.
    .OUTPUTS
    System.String. Absolute path to a hash-validated fixture directory.
    #>
    [CmdletBinding()]
    param([string] $Directory)
    if (!$Directory) {
        $Directory = Join-Path $PSScriptRoot '../../../tmp/SelectString/log-fixture'
        if (!(Test-Path -LiteralPath (Join-Path $Directory 'manifest.json')) -and
            !(Test-Path -LiteralPath (Join-Path $Directory 'application.log'))) {
            New-SelectStringLogFixture -Directory $Directory
        }
    }
    $Directory = (Resolve-Path -LiteralPath $Directory).ProviderPath
    $manifest = Get-Content (Join-Path $Directory 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or (Get-FileHash (Join-Path $Directory 'application.log')).Hash -ne $manifest.SHA256) {
        throw 'Log fixture version/hash mismatch. Regenerate the fixture.'
    }
    return $Directory
}

function Invoke-SelectStringMeasurement {
    <#
.SYNOPSIS
Runs isolated Select-String benchmarks and writes timing/validation JSON.
.DESCRIPTION
Creates small fixtures or reuses a hash-validated log. Each executable/scenario
runs in a fresh child process. Wall time excludes generation, validation and
warmup. Output is discarded; counting/latency is a separate optional pass.
.PARAMETER PwshPath
Executables to compare, in run order.
.PARAMETER Revision
One result label per executable.
.PARAMETER Scenario
Scenario names. Log scenarios use the shared log fixture. Defaults to the small-fixture matrix.
.PARAMETER Iterations
Measured calls per worker (default 7).
.PARAMETER WarmupIterations
Unmeasured calls before sampling (default 3).
.PARAMETER Affinity
Optional process CPU affinity mask; zero leaves scheduling unchanged.
.PARAMETER LineCount
Number of lines generated for the small-fixture matrix (default 100000).
.PARAMETER RecordCount
Input records for PipelineCaptures, PipelineStop, PatternRecords, PatternFile and PatternSmallFiles
(default 10000), shared by one cmdlet invocation.
.PARAMETER LinesPerFile
Records per file in PatternSmallFiles (default 16); the last file may be shorter.
.PARAMETER MatchEvery
One matching record per this many inputs in PipelineCaptures and PipelineStop (default 100).
.PARAMETER PatternFamily
Shared corpus pattern for PatternFile, PatternSmallFiles and PatternRecords (default Literal).
.PARAMETER PatternOutput
Output mode for PatternFile: Matches (default), Context (two lines each side), Raw, List or Quiet.
Other pattern scenarios currently require Matches.
.PARAMETER InputKind
Corpus input shape; Mixed cycles through match, absent, short and long near miss.
.PARAMETER CaseSensitive
Use case-sensitive matching for the pattern-family scenarios. Default is insensitive.
.PARAMETER Culture
Worker culture for regex construction and matching. Default en-US.
.PARAMETER PromotionMode
Optional mode for the isolated promotion build. Other builds ignore this setting.
Warm modes use the scenario's first authored input as an explicit worker probe.
.PARAMETER TimeoutSeconds
Maximum worker lifetime, including validation and warmup (default 60).
.PARAMETER Artifacts
Directory for fixture files, per-case JSON and results.json. Existing files can be replaced.
.PARAMETER Worker
Internal child-process mode. Do not use for ordinary comparisons.
.PARAMETER TraceMarkers
Emit warmup, iteration and counting ETW boundaries outside measured calls.
.PARAMETER SkipCounting
Skip the separate latency/counting pass. Log capture validation still runs.
.PARAMETER LatencyIterations
Repeated instrumented latency/counting calls (default 1). FirstResultMs reports their median.
.PARAMETER FixtureRoot
Internal child-process path to small fixtures.
.PARAMETER LogFixture
Shared log directory. Omit to reuse or create tmp/SelectString/log-fixture.
.PARAMETER OutputFile
Internal child-process JSON output path.
.EXAMPLE
Invoke-SelectStringMeasurement -PwshPath ./tmp/SelectString/candidate/pwsh.exe -Revision candidate -Scenario LogSparse
Runs a log scan; the default fixture is generated if absent.
.NOTES
Default log size is 300 MiB. Benchmarks are warm-cache; peak working set includes
setup and validation. Failed/timed-out workers are recorded as non-Measured results.
#>
[CmdletBinding()]
param(
    [string[]] $PwshPath,
    [string[]] $Revision,
    [string[]] $Scenario = @('String', 'Records', 'SmallFiles', 'LargeFile', 'LongLine', 'Dense', 'NoMatch', 'AllMatches', 'Simple', 'Context', 'DenseContext', 'NoMatchContext', 'Raw', 'List', 'QuietEarly', 'QuietLate', 'Utf16', 'Utf32', 'Legacy', 'Formatting'),
    [ValidateRange(1,100)] [int] $Iterations = 7,
    [ValidateRange(1,1000)] [int] $WarmupIterations = 3,
    [ValidateRange(0,9223372036854775807)] [long] $Affinity = 0,
    [ValidateRange(16,2147483647)] [int] $LineCount = 100000,
    [ValidateRange(1,2147483647)] [int] $RecordCount = 10000,
    [ValidateRange(1,2147483647)] [int] $LinesPerFile = 16,
    [ValidateRange(1,2147483647)] [int] $MatchEvery = 100,
    [ValidateSet('Literal','DotStar','DotPlus','SqlDuration','OrderId','FileExtension','PrReference','Markdown','Assignment')]
    [string] $PatternFamily = 'Literal',
    [ValidateSet('Matches','Context','Raw','List','Quiet')]
    [string] $PatternOutput = 'Matches',
    [ValidateSet('Match','Absent','NearMiss','LongNearMiss','Mixed')]
    [string] $InputKind = 'Mixed',
    [switch] $CaseSensitive,
    [string] $Culture = 'en-US',
    [ValidateSet('interpreted','compiled','background','background-warm','delayed-background-warm','delayed-synchronous')]
    [string] $PromotionMode,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 60,
    [string] $Artifacts = (Join-Path $PSScriptRoot 'bin/measurements'),
    [switch] $Worker,
    [switch] $TraceMarkers,
    [switch] $SkipCounting,
    [ValidateRange(1,100)] [int] $LatencyIterations = 1,
    [string] $FixtureRoot,
    [BenchmarkDirectoryCompletion('Fixture')]
    [string] $LogFixture,
    [string] $OutputFile
)

$ErrorActionPreference = 'Stop'
if ($PatternOutput -ne 'Matches' -and @($Scenario | Where-Object { $_ -ne 'PatternFile' }).Count) {
    throw 'PatternOutput modes require PatternFile.'
}
if (!$Worker) {
    if (!$PwshPath -or $Revision.Count -ne $PwshPath.Count) { throw 'Provide a revision label for each executable.' }
    # .NET's current directory can differ from PowerShell's current location.
    $Artifacts = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Artifacts)
    $PwshPath = @($PwshPath | ForEach-Object { (Resolve-Path -LiteralPath $_ -ErrorAction Stop).ProviderPath })
    if (@($Scenario | Where-Object { $_ -like 'Log*' }).Count) {
        $LogFixture = Resolve-SelectStringLogFixture -Directory $LogFixture
    }
    $FixtureRoot = Join-Path $Artifacts 'fixtures'
    $null = New-Item -ItemType Directory -Path $FixtureRoot -Force
    if (@($Scenario | Where-Object { $_ -notlike 'Log*' }).Count) {
        foreach ($encoding in @(@{ Name = 'large'; Value = [Text.UTF8Encoding]::new($false) }, @{ Name = 'utf16'; Value = [Text.Encoding]::Unicode }, @{ Name = 'utf32'; Value = [Text.Encoding]::UTF32 }, @{ Name = 'legacy'; Value = [Text.Encoding]::GetEncoding(1252) })) {
            $writer = [IO.StreamWriter]::new((Join-Path $FixtureRoot ($encoding.Name + '.txt')), $false, $encoding.Value)
            try {
                for ($i = 0; $i -lt $LineCount; $i++) {
                    $writer.WriteLine($(if ($i % 100 -eq 0) { 'needle café needle' } else { 'ordinary input café without a match' }))
                }
            }
            finally { $writer.Dispose() }
        }
        [IO.File]::WriteAllText((Join-Path $FixtureRoot 'long.txt'), ('x' * (4MB)) + 'needle')
        foreach ($name in 'dense', 'late') {
            $writer = [IO.StreamWriter]::new((Join-Path $FixtureRoot "$name.txt"))
            try {
                for ($i = 0; $i -lt $LineCount; $i++) {
                    if ($name -eq 'dense') { $writer.WriteLine('needle café needle') }
                    else { $writer.Write("ordinary line`n") }
                }
                if ($name -eq 'late') { $writer.Write('needle') }
            }
            finally { $writer.Dispose() }
        }
        $smallLines = [string[]](@('needle café needle') + @('ordinary input café without a match') * 15)
        for ($i = 0; $i -lt 256; $i++) { [IO.File]::WriteAllLines((Join-Path $FixtureRoot "small-$i.txt"), $smallLines) }
    }
    $results = [Collections.Generic.List[object]]::new()
    for ($build = 0; $build -lt $PwshPath.Count; $build++) {
        foreach ($case in $Scenario) {
            $output = Join-Path $Artifacts "$build-$case.json"
            $start = [Diagnostics.ProcessStartInfo]::new($PwshPath[$build])
            $start.WorkingDirectory = $Artifacts
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            foreach ($arg in @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Measure-SelectString.ps1'),'-Worker','-FixtureRoot',$FixtureRoot,'-OutputFile',$output,'-Scenario',$case,'-Iterations',[string]$Iterations,'-WarmupIterations',[string]$WarmupIterations,'-Affinity',[string]$Affinity)) {
                $start.ArgumentList.Add($arg)
            }
            if ($TraceMarkers) { $start.ArgumentList.Add('-TraceMarkers') }
            foreach ($arg in @('-RecordCount', [string]$RecordCount, '-MatchEvery', [string]$MatchEvery, '-LinesPerFile', [string]$LinesPerFile)) { $start.ArgumentList.Add($arg) }
            foreach ($arg in @('-PatternFamily', $PatternFamily, '-InputKind', $InputKind, '-Culture', $Culture, '-PatternOutput', $PatternOutput)) {
                $start.ArgumentList.Add($arg)
            }
            if ($CaseSensitive) { $start.ArgumentList.Add('-CaseSensitive') }
            if ($PromotionMode) {
                $start.ArgumentList.Add('-PromotionMode')
                $start.ArgumentList.Add($PromotionMode)
            }
            if ($SkipCounting) { $start.ArgumentList.Add('-SkipCounting') }
            $start.ArgumentList.Add('-LatencyIterations')
            $start.ArgumentList.Add([string]$LatencyIterations)
            if ($LogFixture) { $start.ArgumentList.Add('-LogFixture'); $start.ArgumentList.Add($LogFixture) }
            $result = [BenchmarkWorker]::Run($start, $TimeoutSeconds, $Affinity, $case, $output)
            $result | Add-Member Revision $Revision[$build]
            $result | Add-Member Executable $PwshPath[$build]
            $results.Add($result)
            Write-Host "$($Revision[$build]) $case : $($result.Status)"
        }
    }
    $results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Artifacts 'results.json')
    $results | Select-Object Revision,Scenario,Status,MedianMs,MiBPerSecond,AllocatedBytes,FirstResultMs,ResultCount | Format-Table -AutoSize
    return
}

$large = Join-Path $FixtureRoot 'large.txt'
$records = [Collections.Generic.List[string]]::new()
if ($Scenario[0] -notlike 'Log*') {
    foreach ($line in [IO.File]::ReadLines($large)) {
        $records.Add($line)
        if ($records.Count -eq 10000) { break }
    }
    $small = [IO.Directory]::GetFiles($FixtureRoot, 'small-*.txt')
    [Array]::Sort($small, [StringComparer]::Ordinal)
}
$logManifest = $null
$validator = $null
$pipelineRecords = $null
$patternFixture = $null
$patternRecords = $null
if ($Scenario[0] -in 'PatternFile','PatternRecords','PatternSmallFiles') {
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($Culture)
    Add-Type -Path (Join-Path $PSScriptRoot 'PatternFamilies.cs')
    $patternFixture = [PowerShell.Benchmarks.SelectString.SearchPatternFixture]::Create($PatternFamily)
    $patternRecords = [string[]]::new($RecordCount)
    $inputIndex = [int][PowerShell.Benchmarks.SelectString.SearchInputKind]$InputKind
    for ($i = 0; $i -lt $RecordCount; $i++) {
        $index = $InputKind -eq 'Mixed' ? $i % $patternFixture.Examples.Length : $inputIndex
        $patternRecords[$i] = $patternFixture.Examples[$index].Input
    }
    $patternParameters = @{
        Pattern = $patternFixture.Pattern
        CaseSensitive = [bool]$CaseSensitive
    }
    switch ($PatternOutput) {
        Context { $patternParameters.Context = 2 }
        Raw { $patternParameters.Raw = $true }
        List { $patternParameters.List = $true }
        Quiet { $patternParameters.Quiet = $true }
    }
    $patternFile = Join-Path $FixtureRoot 'pattern.txt'
    if ($Scenario[0] -eq 'PatternFile') {
        [IO.File]::WriteAllLines($patternFile, $patternRecords, [Text.UTF8Encoding]::new($false))
    }
    if ($Scenario[0] -eq 'PatternSmallFiles') {
        $patternFiles = [Collections.Generic.List[string]]::new()
        for ($offset = 0; $offset -lt $RecordCount; $offset += $LinesPerFile) {
            $path = Join-Path $FixtureRoot ("pattern-{0:D6}.txt" -f $patternFiles.Count)
            $length = [Math]::Min($LinesPerFile, $RecordCount - $offset)
            $chunk = [string[]]::new($length)
            [Array]::Copy($patternRecords, $offset, $chunk, 0, $length)
            [IO.File]::WriteAllLines($path, $chunk, [Text.UTF8Encoding]::new($false))
            $patternFiles.Add($path)
        }
        $patternFilePaths = $patternFiles.ToArray()
    }
}
if ($Scenario[0] -in 'PipelineCaptures','PipelineStop') {
    $pipelineRecords = [string[]]::new($RecordCount)
    for ($i = 0; $i -lt $RecordCount; $i++) {
        $level = if ($i % $MatchEvery -eq 0) { 'ERROR' } else { 'INFO' }
        $pipelineRecords[$i] = "level=$level request=$i duration=$($i % 5000)ms"
    }
    $pipelinePattern = 'level=(?<level>ERROR) request=(?<request>[0-9]+) duration=(?<duration>[0-9]+)ms'
}
if ($Scenario[0] -like 'Log*') {
    if ($Scenario[0] -notin 'LogRare','LogSparse','LogMedium','LogBroad','LogDense','LogAllMatches','LogContext') { throw 'Unknown log scenario' }
    Add-Type -Path (Join-Path $PSScriptRoot 'LogFixture.cs')
    $logManifest = Get-Content (Join-Path $LogFixture 'manifest.json') -Raw | ConvertFrom-Json
    $logParameters = @{
        LiteralPath = Join-Path $LogFixture 'application.log'
        Pattern = [SelectStringLogFixture]::Pattern($Scenario[0])
        CaseSensitive = $true
    }
    if ($Scenario[0] -eq 'LogAllMatches') { $logParameters.AllMatches = $true }
    if ($Scenario[0] -eq 'LogContext') { $logParameters.Context = 2 }
}
$action = switch ($Scenario[0]) {
    { $_ -like 'Log*' } { { Select-String @logParameters } }
    String { { 'needle café needle' | Select-String needle } }
    Records { { $records | Select-String needle } }
    PatternRecords { { $patternRecords | Select-String @patternParameters } }
    PatternFile { { Select-String -LiteralPath $patternFile @patternParameters } }
    PatternSmallFiles { { Select-String -LiteralPath $patternFilePaths @patternParameters } }
    PipelineCaptures { { $pipelineRecords | Select-String -Pattern $pipelinePattern } }
    PipelineStop { { $pipelineRecords | Select-String -Pattern $pipelinePattern | Select-Object -First 1 } }
    FileStop { { Select-String -LiteralPath $large -Pattern '(?<word>needle)' | Select-Object -First 1 } }
    SmallFiles { { Select-String -LiteralPath $small -Pattern needle } }
    LargeFile { { Select-String -LiteralPath $large -Pattern needle } }
    LongLine { { Select-String -LiteralPath (Join-Path $FixtureRoot 'long.txt') -Pattern needle } }
    Dense { { Select-String -LiteralPath (Join-Path $FixtureRoot 'dense.txt') -Pattern needle } }
    NoMatch { { Select-String -LiteralPath $large -Pattern absent } }
    AllMatches { { Select-String -LiteralPath $large -Pattern 'needle','café' -AllMatches } }
    Simple { { Select-String -LiteralPath $large -Pattern needle -SimpleMatch } }
    Context { { Select-String -LiteralPath $large -Pattern needle -Context 2 } }
    DenseContext { { Select-String -LiteralPath (Join-Path $FixtureRoot 'dense.txt') -Pattern needle -Context 2 } }
    NoMatchContext { { Select-String -LiteralPath $large -Pattern absent -Context 2 } }
    Raw { { Select-String -LiteralPath $large -Pattern needle -Raw } }
    List { { Select-String -LiteralPath $small -Pattern needle -List } }
    QuietEarly { { Select-String -LiteralPath $large -Pattern needle -Quiet } }
    QuietLate { { Select-String -LiteralPath (Join-Path $FixtureRoot 'late.txt') -Pattern needle -Quiet } }
    Utf16 { { Select-String -LiteralPath (Join-Path $FixtureRoot 'utf16.txt') -Pattern needle } }
    Utf32 { { Select-String -LiteralPath (Join-Path $FixtureRoot 'utf32.txt') -Pattern needle } }
    Legacy { { Select-String -LiteralPath (Join-Path $FixtureRoot 'legacy.txt') -Encoding 1252 -Pattern needle } }
    Formatting { { Select-String -LiteralPath $large -Pattern needle -Context 2 | ForEach-Object { $_.ToString($FixtureRoot) } } }
    default { throw "Unknown scenario: $Scenario" }
}

$selectStringAssembly = (Get-Command Select-String -CommandType Cmdlet).ImplementingType.Assembly
$promotionDiagnostics = $selectStringAssembly.GetType('Microsoft.PowerShell.Commands.SelectStringPromotionDiagnostics', $false)
if ($PromotionMode) { $env:SELECTSTRING_PROMOTION_MODE = $PromotionMode.ToLowerInvariant() }
if ($promotionDiagnostics -and $PromotionMode -like '*warm') {
    if ($patternRecords) { $warmInput = $patternRecords[0] }
    elseif ($pipelineRecords) { $warmInput = $pipelineRecords[0] }
    elseif ($logManifest) {
        $reader = [IO.File]::OpenText($logParameters.LiteralPath)
        try { $warmInput = $reader.ReadLine() }
        finally { $reader.Dispose() }
    } else { $warmInput = $records[0] }
    $env:SELECTSTRING_PROMOTION_WARM_INPUT = $warmInput
}

# Validate every returned line number and named capture outside all timed phases.
$validatedPipelineResults = $null
if ($patternFixture) {
    $expectedLines = [Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $RecordCount; $i++) {
        $index = $InputKind -eq 'Mixed' ? $i % $patternFixture.Examples.Length : $inputIndex
        if ($null -ne $patternFixture.Examples[$index].Value) { $expectedLines.Add($i + 1) }
    }
    $hasExpectedMatch = $expectedLines.Count -gt 0
    if ($PatternOutput -eq 'List' -and $hasExpectedMatch) {
        $firstLine = $expectedLines[0]
        $expectedLines.Clear()
        $expectedLines.Add($firstLine)
    }
    $script:validatedCount = 0
    & $action | ForEach-Object {
        if ($PatternOutput -eq 'Quiet') {
            if ($_ -isnot [bool] -or $_ -ne $hasExpectedMatch -or $script:validatedCount -ne 0) {
                throw 'Incorrect Quiet result'
            }
            $script:validatedCount++
            return
        }
        if ($script:validatedCount -ge $expectedLines.Count) {
            throw 'Unexpected extra pattern result'
        }
        $recordIndex = $expectedLines[$script:validatedCount] - 1
        $expectedLine = $recordIndex + 1
        if ($Scenario[0] -eq 'PatternSmallFiles') {
            $fileIndex = [int][Math]::Floor($recordIndex / [double]$LinesPerFile)
            $expectedLine = $recordIndex % $LinesPerFile + 1
            if ($_.Path -cne $patternFiles[$fileIndex]) {
                throw 'Incorrect pattern result file/order'
            }
        }
        if ($PatternOutput -eq 'Raw') {
            if ($_ -isnot [string] -or $_ -cne $patternRecords[$recordIndex]) {
                throw 'Incorrect Raw result'
            }
            $script:validatedCount++
            return
        }
        if ($_.LineNumber -ne $expectedLine) {
            throw 'Incorrect pattern result line/order'
        }
        $index = $InputKind -eq 'Mixed' ? $recordIndex % $patternFixture.Examples.Length : $inputIndex
        if ($_.Line -cne $patternFixture.Examples[$index].Input) { throw 'Incorrect pattern input line' }
        [PowerShell.Benchmarks.SelectString.SearchPatternFixture]::ValidateResult($patternFixture.Examples[$index], $_.Matches)
        if ($PatternOutput -eq 'Context') {
            $before = @()
            $after = @()
            for ($n = [Math]::Max(0, $recordIndex - 2); $n -lt $recordIndex; $n++) {
                $before += $patternRecords[$n]
            }
            for ($n = $recordIndex + 1; $n -le [Math]::Min($RecordCount - 1, $recordIndex + 2); $n++) {
                $after += $patternRecords[$n]
            }
            foreach ($side in 'PreContext','PostContext') {
                $expectedContext = $side -eq 'PreContext' ? $before : $after
                $actualContext = $_.Context.$side
                if ($actualContext.Count -ne $expectedContext.Count) {
                    throw 'Incorrect context length'
                }
                for ($n = 0; $n -lt $expectedContext.Count; $n++) {
                    if ($actualContext[$n] -cne $expectedContext[$n]) {
                        throw 'Incorrect context line'
                    }
                }
            }
        }
        $script:validatedCount++
    }
    $expectedResultCount = $PatternOutput -eq 'Quiet' ? 1 : $expectedLines.Count
    if ($script:validatedCount -ne $expectedResultCount) { throw 'Incorrect pattern result count' }
    $validatedPipelineResults = $script:validatedCount
}
if ($Scenario[0] -in 'PipelineCaptures','PipelineStop','FileStop') {
    $script:validatedCount = 0
    & $action | ForEach-Object {
        if ($Scenario[0] -eq 'FileStop') {
            if ($_.LineNumber -ne 1 -or $_.Matches[0].Groups['word'].Value -ne 'needle') { throw 'Incorrect file termination result' }
        } else {
            $index = [long]$script:validatedCount * $MatchEvery
            if ($_.LineNumber -ne ($index + 1) -or $_.Matches.Count -ne 1 -or
                $_.Matches[0].Groups['level'].Value -ne 'ERROR' -or
                $_.Matches[0].Groups['request'].Value -ne [string]$index -or
                $_.Matches[0].Groups['duration'].Value -ne [string]($index % 5000)) { throw 'Incorrect pipeline capture' }
        }
        $script:validatedCount++
    }
    $expected = if ($Scenario[0] -eq 'PipelineCaptures') { [long][Math]::Ceiling($RecordCount / [double]$MatchEvery) } else { 1 }
    if ($script:validatedCount -ne $expected) { throw 'Incorrect pipeline result count' }
    $validatedPipelineResults = $script:validatedCount
}
if ($logManifest) {
    $validator = [SelectStringLogValidator]::new($Scenario[0], $logManifest.Lines)
    & $action | ForEach-Object { $validator.Accept($_.LineNumber, $_.Matches) }
    $validator.Complete()
}

# Startup, fixture creation, module loading and warmup are outside timed samples.
$markers = $null
if ($TraceMarkers) {
    Add-Type -Path (Join-Path $PSScriptRoot 'BenchmarkEvents.cs')
    $markers = [SelectStringBenchmarkEvents]::Log
    if ($markers.ConstructionException) { throw $markers.ConstructionException }
    $markers.WarmupBegin($Scenario[0])
}
try {
    for ($warmup = 0; $warmup -lt $WarmupIterations; $warmup++) { & $action > $null }
} finally {
    if ($markers) { $markers.WarmupEnd($Scenario[0]) }
}
$samples = @()
$allocations = @()
$collections = @()
$process = [Diagnostics.Process]::GetCurrentProcess()
$process.Refresh()
$cpuBefore = $process.TotalProcessorTime
$promotionBefore = $promotionDiagnostics ? $promotionDiagnostics::Snapshot() : $null
$cpuWindow = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Iterations; $i++) {
    if ($markers) { $markers.IterationBegin($i) }
    $before = [GC]::GetTotalAllocatedBytes($true)
    $g0 = [GC]::CollectionCount(0)
    $g1 = [GC]::CollectionCount(1)
    $g2 = [GC]::CollectionCount(2)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $action > $null }
    finally { $timer.Stop() }
    $samples += $timer.Elapsed.TotalMilliseconds
    $allocations += [GC]::GetTotalAllocatedBytes($true) - $before
    $collections += ,@(([GC]::CollectionCount(0) - $g0), ([GC]::CollectionCount(1) - $g1), ([GC]::CollectionCount(2) - $g2))
    if ($markers) { $markers.IterationEnd($i) }
}
$cpuWindow.Stop()
$process.Refresh()
$cpuMs = ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds
$promotionCounters = $null
if ($promotionDiagnostics) {
    $after = $promotionDiagnostics::Snapshot()
    if ($after[7] -ne 0 -or $after[5] -ne $promotionBefore[5]) { throw 'Promotion worker survived cleanup or faulted' }
    $promotionCounters = [ordered]@{
        Completed = $after[0] - $promotionBefore[0]
        Admitted = $after[1] - $promotionBefore[1]
        Started = $after[2] - $promotionBefore[2]
        Rejected = $after[3] - $promotionBefore[3]
        Promoted = $after[4] - $promotionBefore[4]
        Faulted = $after[5] - $promotionBefore[5]
        DrainMs = ($after[6] - $promotionBefore[6]) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
        OutstandingWorkers = $after[7]
    }
}

# Measure streaming latency separately: the counting sink adds its own overhead.
$script:count = $null
$script:first = $null
$latencySamples = @()
if (!$SkipCounting) {
    if ($markers) { $markers.CountingBegin($Scenario[0]) }
    $previousCount = $null
    try {
        for ($sample = 0; $sample -lt $LatencyIterations; $sample++) {
            $script:count = 0
            $script:first = $null
            $latency = [Diagnostics.Stopwatch]::StartNew()
            try {
                & $action | ForEach-Object {
                    if ($script:count -eq 0) { $script:first = $latency.Elapsed.TotalMilliseconds }
                    $script:count++
                }
            } finally { $latency.Stop() }
            if ($null -ne $previousCount -and $previousCount -ne $script:count) { throw 'Result count changed between latency samples' }
            $previousCount = $script:count
            $latencySamples += ,$script:first
        }
    } finally {
        if ($markers) { $markers.CountingEnd($Scenario[0]) }
    }
}
$sortedLatency = @($latencySamples | Where-Object { $null -ne $_ } | Sort-Object)
$firstResultMedian = $sortedLatency.Count ? $sortedLatency[[int][Math]::Floor($sortedLatency.Count / 2)] : $null
$sorted = @($samples | Sort-Object)
[ordered]@{
    Scenario = $Scenario[0]
    Status = 'Measured'
    Cache = 'Warm'
    PowerShell = $PSVersionTable.PSVersion.ToString()
    Runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    ProcessorCount = [Environment]::ProcessorCount
    Processor = $env:PROCESSOR_IDENTIFIER
    RequestedAffinity = $Affinity
    PromotionMode = $promotionDiagnostics ? ($env:SELECTSTRING_PROMOTION_MODE ?? 'background') : $null
    PromotionCounters = $promotionCounters
    CpuWindowMs = $cpuWindow.Elapsed.TotalMilliseconds
    CpuMs = $cpuMs
    CpuMsPerInvocation = $cpuMs / $Iterations
    WarmupIterations = $WarmupIterations
    WorkerPid = $PID
    Sink = 'NullRedirection'
    TraceMarkers = [bool]$TraceMarkers
    CountingMeasured = !$SkipCounting
    HostSupportsVirtualTerminal = $Host.UI.SupportsVirtualTerminal
    AssemblyHash = (Get-FileHash ([Microsoft.PowerShell.Commands.SelectStringCommand].Assembly.Location)).Hash
    Fixture = $logManifest
    PipelineRecordCount = $pipelineRecords ? $RecordCount : $null
    PipelineMatchEvery = $pipelineRecords ? $MatchEvery : $null
    PatternFamily = $patternFixture ? $PatternFamily : $null
    PatternOutput = $patternFixture ? $PatternOutput : $null
    PatternInput = $patternFixture ? $InputKind : $null
    PatternRecordCount = $patternFixture ? $RecordCount : $null
    PatternFileCount = $Scenario[0] -eq 'PatternSmallFiles' ? $patternFiles.Count : $null
    PatternLinesPerFile = $Scenario[0] -eq 'PatternSmallFiles' ? $LinesPerFile : $null
    Pattern = $patternFixture ? $patternFixture.Pattern : $null
    PatternCaseSensitive = $patternFixture ? [bool]$CaseSensitive : $null
    PatternCulture = $patternFixture ? [Globalization.CultureInfo]::CurrentCulture.Name : $null
    ValidatedResults = $validator ? $validator.Count : $validatedPipelineResults
    ValidatedMatches = $validator ? $validator.Captures : $null
    MiBPerSecond = !$logManifest ? $null : $logManifest.Bytes / 1MB / ($sorted[[int][Math]::Floor($sorted.Count / 2)] / 1000)
    SamplesMs = $samples
    MedianMs = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    AllocatedBytes = ($allocations | Measure-Object -Average).Average
    Collections = $collections
    FirstResultMs = $firstResultMedian
    FirstResultSamplesMs = $latencySamples
    LatencyIterations = $SkipCounting ? 0 : $LatencyIterations
    ResultCount = $script:count
    PeakWorkingSetBytes = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputFile -Encoding utf8

}

function Invoke-SelectStringCapture {
    <#
.SYNOPSIS
Captures shipping and a candidate with focused WPR/ETW profiles.
.DESCRIPTION
Run elevated on Windows. Resolves both builds and the log before recording.
Captures each build separately, always labels them shipping/candidate, and stops
its own recording in finally. Does not cancel another active WPR session.
.PARAMETER CandidateDirectory
Child directory under tmp/SelectString. Omit to select the newest eligible build
by Utility DLL timestamp, falling back to pwsh.exe timestamp. Supports completion.
.PARAMETER Scenario
One or more scenarios, run in the supplied order; default DenseContext.
Duplicate scenarios run once. Each scenario captures shipping, then candidate.
.PARAMETER LogFixture
Shared log directory. Omit to reuse/create the default 300 MiB fixture before WPR starts.
.PARAMETER WarmupIterations
Unmeasured calls per build (default 60).
.PARAMETER Iterations
Measured calls per build (default 25).
.PARAMETER TimeoutSeconds
Maximum benchmark worker lifetime in seconds (default 180).
.EXAMPLE
Invoke-SelectStringCapture -Scenario LogAllMatches -WarmupIterations 3 -Iterations 5 -TimeoutSeconds 600
Captures both builds using the default shared log.
.EXAMPLE
Invoke-SelectStringCapture -Scenario LogSparse,LogAllMatches -Iterations 5
Captures shipping and candidate for each scenario in order, using one shared log.
.NOTES
Batches use a subdirectory per scenario; a single scenario retains the existing layout.
Writes ETLs, stop logs and benchmark JSON under tmp/SelectString/traces/<timestamp>.
Requires Windows Performance Toolkit. Event loss or benchmark failure aborts later
captures; startup and validation remain in traces, outside measured markers.
#>
[CmdletBinding()]
param(
    # Build directory name beneath the repository's tmp/SelectString directory.
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]*$')]
    [BenchmarkDirectoryCompletion('Candidate')]
    [string] $CandidateDirectory,
    [ValidateSet('DenseContext','LogRare','LogSparse','LogMedium','LogBroad','LogDense','LogAllMatches','LogContext')]
    [ValidateNotNullOrEmpty()]
    [string[]] $Scenario = @('DenseContext'),
    [BenchmarkDirectoryCompletion('Fixture')]
    [string] $LogFixture,
    [ValidateRange(1,1000)] [int] $WarmupIterations = 60,
    [ValidateRange(1,100)] [int] $Iterations = 25,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$Scenario = @($Scenario | Select-Object -Unique)
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
if (!$CandidateDirectory) {
    $latest = [BenchmarkBuildCatalog]::new((Join-Path $repoRoot 'tmp/SelectString')).Newest()
    $CandidateDirectory = $latest.Name
    Write-Host "Selected newest candidate build: $CandidateDirectory"
}
$profile = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'SelectString.wprp')).ProviderPath
if ($Scenario -like 'Log*') { $LogFixture = Resolve-SelectStringLogFixture -Directory $LogFixture }
$out = Join-Path $repoRoot ("tmp/SelectString/traces/" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Host "Capture directory: $out"

$builds = [ordered]@{
    shipping = './tmp/SelectString/baseline/pwsh.exe'
    candidate = "./tmp/SelectString/$CandidateDirectory/pwsh.exe"
}

# Validate all executables before starting a recording.
foreach ($name in @($builds.Keys)) {
    $builds[$name] = (Resolve-Path -LiteralPath (Join-Path $repoRoot $builds[$name])).ProviderPath
    Write-Host "${name}: $($builds[$name])"
}

foreach ($scenarioName in $Scenario) {
    # Preserve the single-scenario layout; isolate each scenario in a batch.
    $scenarioOut = if ($Scenario.Count -gt 1) { Join-Path $out $scenarioName } else { $out }
    $null = New-Item -ItemType Directory -Path $scenarioOut -Force
    Write-Host "Capturing scenario: $scenarioName"
    foreach ($name in $builds.Keys) {
        # The custom profile limits CLR keywords/level and filters to pwsh.exe.
        wpr.exe -start "${profile}!SelectString" -filemode
        if ($LASTEXITCODE -ne 0) {
            throw "WPR failed to start. Stop here without cancelling any existing recording."
        }

        try {
            Invoke-SelectStringMeasurement `
                -PwshPath $builds[$name] `
                -Revision $name `
                -Scenario $scenarioName -LogFixture $LogFixture `
                -Iterations $Iterations `
                -WarmupIterations $WarmupIterations `
                -Affinity 1 `
                -TraceMarkers -SkipCounting `
                -TimeoutSeconds $TimeoutSeconds `
                -Artifacts (Join-Path $scenarioOut $name)

            if (!$?) { throw "Benchmark failed for $name" }
            $result = Get-Content (Join-Path $scenarioOut "$name/results.json") -Raw | ConvertFrom-Json
            if ($result.Status -ne 'Measured') { throw "Benchmark failed for ${name}: $($result.Status)" }
        }
        finally {
            $stopOutput = @(wpr.exe -stop (Join-Path $scenarioOut "$name-$scenarioName.etl") 2>&1)
            $stopExitCode = $LASTEXITCODE
            $stopOutput | Out-Host
            $stopOutput | Set-Content -LiteralPath (Join-Path $scenarioOut "$name-wpr-stop.log")
            if ($stopExitCode -ne 0) {
                throw "WPR failed to save the trace for $name"
            }

            if (($stopOutput -join "`n") -match 'dropped\s+[1-9][0-9,]*\s+events') {
                throw "WPR reported event loss for $name. Keep the trace and stop log in $out for diagnosis before recording more builds."
            }
        }
    }

}

Write-Host "Results saved to: $out"

}

function New-SelectStringLogFixture {
    <#
.SYNOPSIS
Generates a deterministic UTF-8 application log and manifest.
.DESCRIPTION
Streams log entries with selectable density buckets, named fields, varied messages
and item lists. Writes complete lines until reaching the requested size. Overwrites
application.log and manifest.json in the selected directory.
.PARAMETER Directory
Output directory, default tmp/SelectString/log-fixture relative to the repository.
.PARAMETER SizeMiB
Minimum file size in MiB (1 through 4096; default 300).
.EXAMPLE
New-SelectStringLogFixture -Directory ./tmp/SelectString/log-smoke -SizeMiB 1
Creates a small reproducible smoke-test fixture.
.NOTES
The manifest records bytes, line count, version and SHA256. The workload is synthetic
and regularly spaced; it does not model bursts or multiline exceptions.
#>
[CmdletBinding()]
param(
    [string] $Directory = (Join-Path $PSScriptRoot '../../../tmp/SelectString/log-fixture'),
    [ValidateRange(1,4096)] [int] $SizeMiB = 300
)
$ErrorActionPreference = 'Stop'
$Directory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Directory)
$null = New-Item -ItemType Directory -Path $Directory -Force
Add-Type -Path (Join-Path $PSScriptRoot 'LogFixture.cs')
$path = Join-Path $Directory 'application.log'
$lines = [SelectStringLogFixture]::Generate($path, [long]$SizeMiB * 1MB)
[ordered]@{
    Version = 1; RequestedMiB = $SizeMiB; Lines = $lines
    Bytes = (Get-Item -LiteralPath $path).Length
    SHA256 = (Get-FileHash -LiteralPath $path).Hash
} | ConvertTo-Json | Set-Content (Join-Path $Directory 'manifest.json') -Encoding utf8
Write-Host "Generated $path ($lines lines)"

}

function Invoke-SelectStringComparison {
<#
.SYNOPSIS
Compares Select-String behavior in two PowerShell builds.
.DESCRIPTION
Creates shared fixtures and runs each executable in a fresh process. Compares
serialized public results, captures, context, errors, formatting and input modes,
including large-file decoding and early termination. This is not a timing test.
.PARAMETER Baseline
Path to the shipping PowerShell executable.
.PARAMETER Candidate
Path to the candidate PowerShell executable.
.PARAMETER Artifacts
Directory for shared fixtures and comparison snapshots. Existing files are replaced.
.PARAMETER Worker
Internal child-process mode.
.PARAMETER FixtureRoot
Internal child-process path to shared fixtures.
.PARAMETER OutputFile
Internal child-process snapshot path.
.EXAMPLE
Invoke-SelectStringComparison -Baseline ./tmp/SelectString/baseline/pwsh.exe -Candidate ./tmp/SelectString/candidate/pwsh.exe
#>
[CmdletBinding()]
param(
    [string] $Baseline,
    [string] $Candidate,
    [string] $Artifacts = (Join-Path $PSScriptRoot '../../SelectString/bin/comparison'),
    [switch] $Worker,
    [string] $FixtureRoot,
    [string] $OutputFile
)

$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!$Baseline -or !$Candidate) { throw 'Specify both -Baseline and -Candidate pwsh executables.' }
    $Artifacts = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Artifacts)
    $null = New-Item -ItemType Directory -Path $Artifacts -Force
    $FixtureRoot = Join-Path $Artifacts 'fixtures'
    $null = New-Item -ItemType Directory -Path $FixtureRoot -Force
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'one.txt'), "zero`nhit aa`nhit ab`nthree`nlast hit")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'two.txt'), "hit new`nend")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'none.txt'), "zero`none")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'literal[1].txt'), 'hit literal')
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'utf16.txt'), "é`r`nhit café`rfinal", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllBytes((Join-Path $FixtureRoot 'malformed.txt'), [byte[]](0x68,0x69,0x74,0xff,0x0a))
    $largeText = "hit first`n" + ("ordinary caf$([char]0xe9) " + ('x' * 1000) + "`n") * 2048 + 'hit last'
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'buffer-large.txt'), $largeText)
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'buffer-large-bom.txt'), $largeText, [Text.Encoding]::Unicode)
    $strictBytes = [Text.Encoding]::UTF8.GetBytes("hit first`n" + ('x' * 2MB))
    $strictBytes[8192] = 0xff
    [IO.File]::WriteAllBytes((Join-Path $FixtureRoot 'buffer-large-invalid.txt'), $strictBytes)
    foreach ($entry in @(@{ Name = 'baseline'; Exe = $Baseline }, @{ Name = 'candidate'; Exe = $Candidate })) {
        & $entry.Exe -NoLogo -NoProfile -File (Join-Path $PSScriptRoot '../../SelectString/Compare-SelectString.ps1') -Worker -FixtureRoot $FixtureRoot -OutputFile (Join-Path $Artifacts ($entry.Name + '.json'))
        if ($LASTEXITCODE) { throw "$($entry.Name) worker failed: $LASTEXITCODE" }
    }

    $left = Get-Content -Raw (Join-Path $Artifacts 'baseline.json') | ConvertFrom-Json
    $right = Get-Content -Raw (Join-Path $Artifacts 'candidate.json') | ConvertFrom-Json
    $failures = @()
    foreach ($property in $left.PSObject.Properties) {
        $expected = ConvertTo-Json -InputObject $property.Value -Depth 100 -Compress
        $actual = ConvertTo-Json -InputObject $right.($property.Name) -Depth 100 -Compress
        if ($expected -cne $actual) { $failures += $property.Name }
    }

    if ($failures.Count) { throw "Baseline differences: $($failures -join ', '). See $Artifacts" }
    "Passed $($left.PSObject.Properties.Name.Count) baseline comparisons. Snapshots: $Artifacts"
    return
}

$PSStyle.OutputRendering = 'Ansi'
$null = Get-Command Select-String
$one = Join-Path $FixtureRoot 'one.txt'
$two = Join-Path $FixtureRoot 'two.txt'
$none = Join-Path $FixtureRoot 'none.txt'
$literal = Join-Path $FixtureRoot 'literal[1].txt'
$fileInfo = [IO.FileInfo]::new($one)
$snapshot = [ordered]@{}

function Convert-Result($value) {
    if ($null -eq $value) { return [ordered]@{ Kind = 'Null' } }
    if ($value -is [Management.Automation.ErrorRecord]) {
        return [ordered]@{ Kind = 'Error'; Id = $value.FullyQualifiedErrorId; Category = [string]$value.CategoryInfo.Category; Exception = $value.Exception.GetType().FullName; Target = [string]$value.TargetObject }
    }
    if ($value -is [Management.Automation.WarningRecord]) {
        return [ordered]@{ Kind = 'Warning'; Text = $value.Message }
    }
    if ($value -is [Microsoft.PowerShell.Commands.MatchInfo]) {
        return [ordered]@{
            Kind = 'Match'; Line = $value.Line; Number = $value.LineNumber; Pattern = $value.Pattern
            Path = $value.Path; Filename = $value.Filename; IgnoreCase = $value.IgnoreCase
            Context = $value.Context
            Matches = @($value.Matches | ForEach-Object {
                [ordered]@{ Index = $_.Index; Length = $_.Length; Value = $_.Value; Groups = @($_.Groups | ForEach-Object {
                    [ordered]@{ Name = $_.Name; Success = $_.Success; Index = $_.Index; Length = $_.Length; Captures = @($_.Captures | ForEach-Object { [ordered]@{ Index = $_.Index; Length = $_.Length; Value = $_.Value } }) }
                }) }
            })
            Text = $value.ToString(); Relative = $value.ToString($FixtureRoot); Emphasis = $value.ToEmphasizedString($FixtureRoot)
        }
    }
    return [ordered]@{ Kind = $value.GetType().FullName; Value = $value }
}

function Add-Case([string] $name, [scriptblock] $action) {
    $values = @(try { & $action 2>&1 3>&1 } catch { $_ })
    $snapshot[$name] = @($values | ForEach-Object {
        try { Convert-Result $_ }
        catch { [ordered]@{ Kind = 'SnapshotError'; Exception = $_.Exception.GetType().FullName; Message = $_.Exception.Message } }
    })
}

$snapshot['PublicApi'] = @([Microsoft.PowerShell.Commands.MatchInfo], [Microsoft.PowerShell.Commands.MatchInfoContext] | ForEach-Object {
    $_.GetMembers([Reflection.BindingFlags]'Public,Instance,DeclaredOnly') | ForEach-Object ToString | Sort-Object
})
foreach ($mode in @('Default', 'SimpleMatch', 'AllMatches', 'NotMatch', 'Raw', 'List', 'Quiet', 'ListQuiet')) {
    foreach ($context in @(@(0,0), @(2,0), @(0,2), @(2,2))) {
        $options = @{ Pattern = @('hit', '(?<letters>a)+'); Context = $context }
        if ($mode -eq 'ListQuiet') { $options.List = $true; $options.Quiet = $true }
        elseif ($mode -ne 'Default') { $options[$mode] = $true }
        $suffix = "$mode-$($context -join '-')"
        Add-Case "files-$suffix" { Select-String -LiteralPath $one,$two @options }
        Add-Case "records-$suffix" { 'zero','hit aa','hit ab','three','last hit' | Select-String @options }
        Add-Case "none-$suffix" { Select-String -LiteralPath $none @options }
    }
}
Add-Case 'single-record' { Select-String -InputObject "before`nhit`nafter" -Pattern hit -Context 1 }
Add-Case 'filename-is-text' { $one | Select-String -Pattern one }
Add-Case 'fileinfo-path' { Select-String -Path $fileInfo -Pattern hit }
Add-Case 'fileinfo-literalpath' { Select-String -LiteralPath $fileInfo -Pattern hit }
Add-Case 'wrapped-fileinfo' { Select-String -LiteralPath ([Management.Automation.PSObject]::AsPSObject($fileInfo)) -Pattern hit }
Add-Case 'mixed-path-list' { Select-String -Path @($fileInfo, $two) -Pattern hit }
Add-Case 'property-path' { [pscustomobject]@{ Path = $fileInfo } | Select-String -Pattern hit }
Add-Case 'property-literalpath' { [pscustomobject]@{ LiteralPath = $fileInfo } | Select-String -Pattern hit }
Add-Case 'property-pspath' { [pscustomobject]@{ PSPath = $one } | Select-String -Pattern hit }
Add-Case 'fileinfo-input' { $fileInfo | Select-String -Pattern hit }
Add-Case 'mixed-input' { 'before', $fileInfo, 'hit record', 'after' | Select-String -Pattern hit -Context 1 }
Add-Case 'literal-brackets' { Select-String -LiteralPath ([IO.FileInfo]::new($literal)) -Pattern hit }
Add-Case 'wildcards' { Select-String -Path (Join-Path $FixtureRoot '*.txt') -Pattern hit -Include '*one*','*two*' -Exclude '*two*' }
Add-Case 'utf16-bom-override' { Select-String -LiteralPath (Join-Path $FixtureRoot 'utf16.txt') -Encoding ascii -Pattern hit -Context 1 }
Add-Case 'malformed-default' { Select-String -LiteralPath (Join-Path $FixtureRoot 'malformed.txt') -Pattern hit }
foreach ($mode in 'Default','Raw','List','Quiet') {
    $bufferOptions = @{ Pattern = '(?<word>hit)'; LiteralPath = (Join-Path $FixtureRoot 'buffer-large.txt'); Context = 1 }
    if ($mode -ne 'Default') { $bufferOptions[$mode] = $true }
    Add-Case "buffer-large-$mode" { Select-String @bufferOptions }
}
Add-Case 'buffer-large-bom' { Select-String -LiteralPath (Join-Path $FixtureRoot 'buffer-large-bom.txt') -Pattern hit -Context 1 }
$bufferInvalid = Join-Path $FixtureRoot 'buffer-large-invalid.txt'
Add-Case 'buffer-invalid-replacement' { Select-String -LiteralPath $bufferInvalid -Pattern hit }
Add-Case 'buffer-invalid-strict-quiet' { Select-String -LiteralPath $bufferInvalid -Pattern hit -Quiet -Encoding ([Text.UTF8Encoding]::new($false, $true)) }
Add-Case 'buffer-invalid-strict-sink-stop' { Select-String -LiteralPath $bufferInvalid -Pattern hit -Encoding ([Text.UTF8Encoding]::new($false, $true)) | Select-Object -First 1 }
Add-Case 'null' { $null | Select-String -Pattern '^$' }
Add-Case 'empty' { '' | Select-String -Pattern '^$' }
Add-Case 'input-array' { Select-String -InputObject @('hit', 'other') -Pattern hit }
Add-Case 'case-sensitive' { 'HIT','hit' | Select-String -Pattern hit -CaseSensitive }
Add-Case 'culture' { 'I','i','ı' | Select-String -Pattern I -SimpleMatch -Culture tr-TR }
Add-Case 'invalid-regex' { '' | Select-String -Pattern '[' }
Add-Case 'missing-file' { Select-String -LiteralPath (Join-Path $FixtureRoot 'absent.txt') -Pattern hit }
Add-Case 'error-order' { Select-String -LiteralPath $one,(Join-Path $FixtureRoot 'absent.txt'),$two -Pattern hit -ErrorAction Continue }
Add-Case 'non-filesystem-provider' { Select-String -Path Env:PATH -Pattern hit -ErrorAction Continue }
Add-Case 'directory' { Select-String -Path $FixtureRoot -Pattern hit -Quiet }
Add-Case 'raw-quiet-binding' { 'hit' | Select-String hit -Raw -Quiet }
Add-Case 'filter-context' { Select-String -LiteralPath $one -Pattern hit -Context 1 | Select-String -Pattern aa -Context 2 }
Add-Case 'filter-identity' {
    $original = 'hit' | Select-String hit
    $filtered = $original | Select-String hit
    [object]::ReferenceEquals($original, $filtered)
}
Add-Case 'filter-cloning' {
    $original = 'before','hit','after' | Select-String hit -Context 1
    $filtered = $original | Select-String hit
    [object]::ReferenceEquals($original, $filtered)
    $original
    $filtered
}
foreach ($mode in @('Raw', 'Quiet', 'List', 'ListQuiet')) {
    $filterOptions = @{ Pattern = 'hit'; Context = @(2,1) }
    if ($mode -eq 'ListQuiet') { $filterOptions.List = $true; $filterOptions.Quiet = $true }
    else { $filterOptions[$mode] = $true }
    Add-Case "filter-mode-$mode" {
        Select-String -LiteralPath $one -Pattern hit -Context 1 | Select-String @filterOptions
    }
    Add-Case "mixed-filter-mode-$mode" {
        $existing = 'hit existing' | Select-String hit
        'before', $existing, 'hit record', 'after' | Select-String @filterOptions
    }
}
Add-Case 'downstream-line-mutation' {
    'hit first','hit second','hit third' | Select-String hit -Context 1,0 | ForEach-Object {
        $_.Line = $_.Line.ToUpperInvariant()
        $_
    }
}
Add-Case 'context-ring-wraparound' {
    0..49 | ForEach-Object { if ($_ % 7 -lt 2) { "hit $_" } else { "line $_" } } | Select-String hit -Context 3
}
Add-Case 'downstream-stop' { Select-String -LiteralPath $one -Pattern hit | Select-Object -First 1 }
Add-Case 'list-file-closed' {
    Select-String -LiteralPath $one -Pattern hit -List | ForEach-Object {
        $stream = [IO.File]::Open($one, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Dispose()
        $_
    }
}
$snapshot | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputFile -Encoding utf8

}

# Bind custom attribute metadata while module-local class names are in scope.
foreach ($commandName in 'Invoke-SelectStringCapture','Invoke-SelectStringMeasurement','New-SelectStringLogFixture','Invoke-SelectStringComparison') {
    $null = (Get-Command $commandName).Parameters
}

Export-ModuleMember -Function Invoke-SelectStringComparison,Invoke-SelectStringMeasurement,Invoke-SelectStringCapture,New-SelectStringLogFixture,New-SelectStringScriptParameters
