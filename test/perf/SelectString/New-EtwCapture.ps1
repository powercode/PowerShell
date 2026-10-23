# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [ValidateSet('DenseContext','LogRare','LogSparse','LogMedium','LogBroad','LogDense','LogAllMatches','LogContext')]
    [string] $Scenario = 'DenseContext',
    [string] $LogFixture,
    [ValidateRange(1,1000)] [int] $WarmupIterations = 60,
    [ValidateRange(1,100)] [int] $Iterations = 25,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
$benchmark = Join-Path $PSScriptRoot 'Measure-SelectString.ps1'
$profile = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'SelectString.wprp')).ProviderPath
if ($Scenario -like 'Log*') {
    if (!$LogFixture) { throw 'Supply -LogFixture with the generated log directory.' }
    $LogFixture = (Resolve-Path -LiteralPath $LogFixture).ProviderPath
    $manifest = Get-Content (Join-Path $LogFixture 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or (Get-FileHash (Join-Path $LogFixture 'application.log')).Hash -ne $manifest.SHA256) {
        throw 'Log fixture version/hash mismatch.'
    }
}
$out = Join-Path $repoRoot ("tmp/SelectString/traces/" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Host "Capture directory: $out"

$builds = [ordered]@{
    shipping          = './tmp/SelectString/baseline/pwsh.exe'
    extraction        = './tmp/SelectString/first-extraction/pwsh.exe'
    'concrete-queues' = './tmp/SelectString/concrete-queues/pwsh.exe'
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
        & $benchmark `
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
