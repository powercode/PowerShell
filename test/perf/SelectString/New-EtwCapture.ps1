# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    # Build directory name beneath the repository's tmp/SelectString directory.
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]*$')]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        $scriptDirectory = Split-Path (Get-Command $commandAst.CommandElements[0].Value -ErrorAction Stop).Path
        $buildRoot = Join-Path $scriptDirectory '../../../tmp/SelectString'
        Get-ChildItem -LiteralPath $buildRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -notin 'baseline','first-extraction','experiment','shipping' -and
                $_.Name -like "$wordToComplete*" -and
                (Test-Path -LiteralPath (Join-Path $_.FullName 'pwsh.exe'))
            } | Sort-Object Name | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_.Name, $_.Name, 'ParameterValue', $_.FullName)
            }
    })]
    [string] $CandidateDirectory,
    [ValidateSet('DenseContext','LogRare','LogSparse','LogMedium','LogBroad','LogDense','LogAllMatches','LogContext')]
    [string] $Scenario = 'DenseContext',
    [string] $LogFixture,
    [ValidateRange(1,1000)] [int] $WarmupIterations = 60,
    [ValidateRange(1,100)] [int] $Iterations = 25,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
if (!$CandidateDirectory) {
    $latest = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tmp/SelectString') -Directory |
        Where-Object {
            $_.Name -notin 'baseline','first-extraction','experiment','shipping' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'pwsh.exe'))
        } | Sort-Object @{
            Expression = {
                $assembly = Get-Item -LiteralPath (Join-Path $_.FullName 'Microsoft.PowerShell.Commands.Utility.dll') -ErrorAction SilentlyContinue
                if ($assembly) { $assembly.LastWriteTimeUtc }
                else { (Get-Item -LiteralPath (Join-Path $_.FullName 'pwsh.exe')).LastWriteTimeUtc }
            }; Descending = $true
        }, Name | Select-Object -First 1
    if (!$latest) { throw 'No candidate build found under tmp/SelectString. Build PowerShell first.' }
    $CandidateDirectory = $latest.Name
    Write-Host "Selected newest candidate build: $CandidateDirectory"
}
$benchmark = Join-Path $PSScriptRoot 'Measure-SelectString.ps1'
$profile = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'SelectString.wprp')).ProviderPath
if ($Scenario -like 'Log*') {
    if (!$LogFixture) {
        $LogFixture = Join-Path $PSScriptRoot '../../../tmp/SelectString/log-fixture'
        if (!(Test-Path -LiteralPath (Join-Path $LogFixture 'manifest.json')) -and
            !(Test-Path -LiteralPath (Join-Path $LogFixture 'application.log'))) {
            & (Join-Path $PSScriptRoot 'New-LogFixture.ps1') -Directory $LogFixture
        }
    }
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
