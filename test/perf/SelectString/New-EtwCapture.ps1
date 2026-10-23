# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
$benchmark = Join-Path $PSScriptRoot 'Measure-SelectString.ps1'
$profile = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'SelectString.wprp')).ProviderPath
$out = Join-Path $repoRoot ("tmp/SelectString/traces/" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Host "Capture directory: $out"

$builds = [ordered]@{
    shipping   = './tmp/SelectString/baseline/pwsh.exe'
    extraction = './tmp/SelectString/first-extraction/pwsh.exe'
    candidate  = './tmp/SelectString/candidate/pwsh.exe'
}

# Validate all executables before starting a recording.
foreach ($name in @($builds.Keys)) {
    $builds[$name] = (Resolve-Path -LiteralPath (Join-Path $repoRoot $builds[$name])).ProviderPath
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
            -Scenario DenseContext `
            -Iterations 25 `
            -WarmupIterations 60 `
            -Affinity 1 `
            -TraceMarkers -SkipCounting `
            -TimeoutSeconds 180 `
            -Artifacts (Join-Path $out $name)

        if (!$?) { throw "Benchmark failed for $name" }
    }
    finally {
        $stopOutput = @(wpr.exe -stop (Join-Path $out "$name-DenseContext.etl") 2>&1)
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
