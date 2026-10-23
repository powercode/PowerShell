# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

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
    [string] $OutputFile
)

$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!$PwshPath -or $Revision.Count -ne $PwshPath.Count) { throw 'Provide a revision label for each executable.' }
    # .NET's current directory can differ from PowerShell's current location.
    $Artifacts = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Artifacts)
    $PwshPath = @($PwshPath | ForEach-Object { (Resolve-Path -LiteralPath $_ -ErrorAction Stop).ProviderPath })
    $FixtureRoot = Join-Path $Artifacts 'fixtures'
    $null = New-Item -ItemType Directory -Path $FixtureRoot -Force
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
    $results = [Collections.Generic.List[object]]::new()
    for ($build = 0; $build -lt $PwshPath.Count; $build++) {
        foreach ($case in $Scenario) {
            $output = Join-Path $Artifacts "$build-$case.json"
            $start = [Diagnostics.ProcessStartInfo]::new($PwshPath[$build])
            $start.WorkingDirectory = $Artifacts
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            foreach ($arg in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-Worker','-FixtureRoot',$FixtureRoot,'-OutputFile',$output,'-Scenario',$case,'-Iterations',[string]$Iterations,'-WarmupIterations',[string]$WarmupIterations,'-Affinity',[string]$Affinity)) {
                $start.ArgumentList.Add($arg)
            }
            if ($TraceMarkers) { $start.ArgumentList.Add('-TraceMarkers') }
            if ($SkipCounting) { $start.ArgumentList.Add('-SkipCounting') }
            $process = [Diagnostics.Process]::Start($start)
            try {
                if ($Affinity) { $process.ProcessorAffinity = [IntPtr]$Affinity }
                if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
                    $process.Kill($true)
                    $process.WaitForExit()
                    $result = [pscustomobject]@{ Scenario = $case; Status = 'Timeout' }
                }
                elseif ($process.ExitCode) {
                    $result = [pscustomobject]@{ Scenario = $case; Status = "Exit $($process.ExitCode)" }
                }
                else { $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json }
            }
            finally { $process.Dispose() }
            $result | Add-Member Revision $Revision[$build]
            $result | Add-Member Executable $PwshPath[$build]
            $results.Add($result)
            Write-Host "$($Revision[$build]) $case : $($result.Status)"
        }
    }
    $results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Artifacts 'results.json')
    $results | Select-Object Revision,Scenario,Status,MedianMs,AllocatedBytes,FirstResultMs,ResultCount | Format-Table -AutoSize
    return
}

$large = Join-Path $FixtureRoot 'large.txt'
$records = [Collections.Generic.List[string]]::new()
foreach ($line in [IO.File]::ReadLines($large)) {
    $records.Add($line)
    if ($records.Count -eq 10000) { break }
}
$small = [IO.Directory]::GetFiles($FixtureRoot, 'small-*.txt')
[Array]::Sort($small, [StringComparer]::Ordinal)
$action = switch ($Scenario[0]) {
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

# Startup, fixture creation, module loading and warmup are outside timed samples.
$markers = $null
if ($TraceMarkers) {
    Add-Type -Path (Join-Path $PSScriptRoot 'BenchmarkEvents.cs')
    $markers = [SelectStringBenchmarkEvents]::Log
    if ($markers.ConstructionException) { throw $markers.ConstructionException }
    $markers.WarmupBegin($Scenario[0])
}
try {
    for ($warmup = 0; $warmup -lt $WarmupIterations; $warmup++) { $null = & $action }
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
    try { $null = & $action }
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
    WorkerPid = $PID; Sink = 'NullAssignment'; TraceMarkers = [bool]$TraceMarkers
    CountingMeasured = !$SkipCounting
    AssemblyHash = (Get-FileHash ([Microsoft.PowerShell.Commands.SelectStringCommand].Assembly.Location)).Hash
    SamplesMs = $samples; MedianMs = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    AllocatedBytes = ($allocations | Measure-Object -Average).Average; Collections = $collections
    FirstResultMs = $script:first; ResultCount = $script:count
    PeakWorkingSetBytes = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputFile -Encoding utf8
