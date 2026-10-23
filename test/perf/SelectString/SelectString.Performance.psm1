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
    Module implementation to delegate to. Only the three public entry points are accepted.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Invoke-SelectStringMeasurement','Invoke-SelectStringCapture','New-SelectStringLogFixture')]
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
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 60,
    [string] $Artifacts = (Join-Path $PSScriptRoot 'bin/measurements'),
    [switch] $Worker,
    [switch] $TraceMarkers,
    [switch] $SkipCounting,
    [string] $FixtureRoot,
    [BenchmarkDirectoryCompletion('Fixture')]
    [string] $LogFixture,
    [string] $OutputFile
)

$ErrorActionPreference = 'Stop'
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
            if ($SkipCounting) { $start.ArgumentList.Add('-SkipCounting') }
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

# Validate every returned line number and named capture outside all timed phases.
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
for ($i = 0; $i -lt $Iterations; $i++) {
    if ($markers) { $markers.IterationBegin($i) }
    $before = [GC]::GetTotalAllocatedBytes($true)
    $g0 = [GC]::CollectionCount(0); $g1 = [GC]::CollectionCount(1); $g2 = [GC]::CollectionCount(2)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $action > $null }
    finally { $timer.Stop() }
    $samples += $timer.Elapsed.TotalMilliseconds
    $allocations += [GC]::GetTotalAllocatedBytes($true) - $before
    $collections += ,@(([GC]::CollectionCount(0) - $g0), ([GC]::CollectionCount(1) - $g1), ([GC]::CollectionCount(2) - $g2))
    if ($markers) { $markers.IterationEnd($i) }
}

# Measure streaming latency separately: the counting sink adds its own overhead.
$script:count = $null
$script:first = $null
if (!$SkipCounting) {
    if ($markers) { $markers.CountingBegin($Scenario[0]) }
    $script:count = 0
    $latency = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $action | ForEach-Object {
            if ($script:count -eq 0) { $script:first = $latency.Elapsed.TotalMilliseconds }
            $script:count++
        }
    } finally {
        $latency.Stop()
        if ($markers) { $markers.CountingEnd($Scenario[0]) }
    }
}
$sorted = @($samples | Sort-Object)
[ordered]@{
    Scenario = $Scenario[0]; Status = 'Measured'; Cache = 'Warm'; PowerShell = $PSVersionTable.PSVersion.ToString()
    Runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    ProcessorCount = [Environment]::ProcessorCount; Processor = $env:PROCESSOR_IDENTIFIER
    RequestedAffinity = $Affinity; WarmupIterations = $WarmupIterations
    WorkerPid = $PID; Sink = 'NullRedirection'; TraceMarkers = [bool]$TraceMarkers
    CountingMeasured = !$SkipCounting
    HostSupportsVirtualTerminal = $Host.UI.SupportsVirtualTerminal
    AssemblyHash = (Get-FileHash ([Microsoft.PowerShell.Commands.SelectStringCommand].Assembly.Location)).Hash
    Fixture = $logManifest
    ValidatedResults = $(if ($validator) { $validator.Count } else { $null })
    ValidatedMatches = $(if ($validator) { $validator.Captures } else { $null })
    MiBPerSecond = $(if ($logManifest) { $logManifest.Bytes / 1MB / ($sorted[[int][Math]::Floor($sorted.Count / 2)] / 1000) } else { $null })
    SamplesMs = $samples; MedianMs = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    AllocatedBytes = ($allocations | Measure-Object -Average).Average; Collections = $collections
    FirstResultMs = $script:first; ResultCount = $script:count
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
DenseContext or a large-log scenario; default DenseContext.
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
.NOTES
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
    [string] $Scenario = 'DenseContext',
    [BenchmarkDirectoryCompletion('Fixture')]
    [string] $LogFixture,
    [ValidateRange(1,1000)] [int] $WarmupIterations = 60,
    [ValidateRange(1,100)] [int] $Iterations = 25,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
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
            -Scenario $Scenario -LogFixture $LogFixture `
            -Iterations $Iterations `
            -WarmupIterations $WarmupIterations `
            -Affinity 1 `
            -TraceMarkers -SkipCounting `
            -TimeoutSeconds $TimeoutSeconds `
            -Artifacts (Join-Path $out $name)

        if (!$?) { throw "Benchmark failed for $name" }
        $result = Get-Content (Join-Path $out "$name/results.json") -Raw | ConvertFrom-Json
        if ($result.Status -ne 'Measured') { throw "Benchmark failed for ${name}: $($result.Status)" }
    }
    finally {
        $stopOutput = @(wpr.exe -stop (Join-Path $out "$name-$Scenario.etl") 2>&1)
        $stopExitCode = $LASTEXITCODE
        $stopOutput | Out-Host
        $stopOutput | Set-Content -LiteralPath (Join-Path $out "$name-wpr-stop.log")
        if ($stopExitCode -ne 0) {
            throw "WPR failed to save the trace for $name"
        }

        if (($stopOutput -join "`n") -match 'dropped\s+[1-9][0-9,]*\s+events') {
            throw "WPR reported event loss for $name. Keep the trace and stop log in $out for diagnosis before recording more builds."
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

# Bind custom attribute metadata while module-local class names are in scope.
foreach ($commandName in 'Invoke-SelectStringCapture','Invoke-SelectStringMeasurement','New-SelectStringLogFixture') {
    $null = (Get-Command $commandName).Parameters
}

Export-ModuleMember -Function Invoke-SelectStringMeasurement,Invoke-SelectStringCapture,New-SelectStringLogFixture,New-SelectStringScriptParameters
