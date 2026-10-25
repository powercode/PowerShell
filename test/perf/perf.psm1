# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

$repoRoot = git rev-parse --show-toplevel
Import-Module "$repoRoot/build.psm1"

$script:BenchmarkFilterCompletionCache = $null

function Get-BenchmarkFilterCompletion
{
    param(
        [string] $WordToComplete
    )

    if ($null -eq $script:BenchmarkFilterCompletionCache) {
        $artifactsPath = Join-Path ([System.IO.Path]::GetTempPath()) "powershell-benchmark-completion-$([guid]::NewGuid().ToString('N'))"
        try {
            $script:BenchmarkFilterCompletionCache = @(Start-Benchmarking -List flat -Quiet -Artifacts $artifactsPath)
        }
        finally {
            if (Test-Path -LiteralPath $artifactsPath) {
                Remove-Item -LiteralPath $artifactsPath -Recurse -Force -ErrorAction Stop
            }
        }
    }

    $searchTerm = $WordToComplete.Trim([char[]]@("'", '"')) -replace '[*?]', ''
    foreach ($benchmarkName in $script:BenchmarkFilterCompletionCache) {
        if ([string]::IsNullOrEmpty($searchTerm) -or $benchmarkName.IndexOf($searchTerm, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            [System.Management.Automation.CompletionResult]::new(
                $benchmarkName,
                $benchmarkName,
                [System.Management.Automation.CompletionResultType]::ParameterValue,
                $benchmarkName)
        }
    }
}

function Start-Benchmarking
{
    <#
    .SYNOPSIS
    Start a benchmark run.

    .PARAMETER TargetPSVersion
    The version of 'Microsoft.PowerShell.SDK' package that we want the benchmark to target.
    The supported versions are 7.0.x and above, including preview versions.

    .PARAMETER TargetFramework
    The target framework to run benchmarks against.

    .PARAMETER List
    List the available benchmarks, in either 'flat' or 'tree' views.

    .PARAMETER Runtime
    Run benchmarks against multiple .NET runtimes.

    .PARAMETER Filter
    One or more wildcard patterns to filter the benchmarks to be executed or to be listed.
    Tab completion lists available benchmarks from the current source build using
    'Start-Benchmarking -List flat'. The list is cached for the current module session.

    .PARAMETER Artifacts
    Path to the folder where you want to store the artifacts produced from running benchmarks.

    .PARAMETER KeepFiles
    Indicates to keep all temporary files produced for running benchmarks.

    .PARAMETER Quiet
        Suppress log output messages when listing benchmarks.
    .EXAMPLE
    Import-Module ./test/perf/perf.psm1
    Start-Benchmarking -TargetFramework net11.0 -Filter '*ParameterBindingIsolation*'

    Runs the source-build parameter-binding isolation benchmarks against the current code.
    BenchmarkDotNet writes artifacts to test/perf/BenchmarkDotNet.Artifacts by default.

    .EXAMPLE
    Import-Module ./test/perf/perf.psm1
    Start-Benchmarking -TargetFramework net11.0 -Filter '*PureNamedBinding_3Params*' -Artifacts ./artifacts/ParameterBinding

    Runs one direct parameter-binding benchmark and writes artifacts to the specified directory.
    #>
    [CmdletBinding(DefaultParameterSetName = 'TargetFramework')]
    param(
        [Parameter(ParameterSetName = 'TargetPSVersion')]
        [ValidatePattern(
            '^7\.\d+\.\d+(-preview\.\d{1,2})?$',
            ErrorMessage = 'The package version is invalid or not supported')]
        [string] $TargetPSVersion,

        [Parameter(ParameterSetName = 'TargetFramework')]
        [ValidateSet('netcoreapp3.1', 'net5.0', 'net6.0', 'net8.0', 'net9.0', 'net10.0', 'net11.0')]
        [string] $TargetFramework = 'net11.0',

        [Parameter(ParameterSetName = 'TargetFramework')]
        [ValidateSet('flat', 'tree')]
        [string] $List,

        [Parameter(Mandatory, ParameterSetName = 'Runtimes')]
        [ValidateSet('netcoreapp3.1', 'net5.0', 'net6.0', 'net8.0', 'net9.0', 'net10.0', 'net11.0')]
        [string[]] $Runtime,

        [System.Management.Automation.ArgumentCompleter({
            param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
            $module = (Get-Command -Name $commandName -CommandType Function).Module
            & $module {
                param($WordToComplete)
                Get-BenchmarkFilterCompletion -WordToComplete $WordToComplete
            } $wordToComplete
        })]
        [string[]] $Filter = '*',
        [string] $Artifacts,
        [switch] $KeepFiles,
        [Parameter(ParameterSetName = 'TargetFramework')]
        [switch] $Quiet
    )

    Begin {
        Find-Dotnet

        if ($Artifacts) {
            $Artifacts = $PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Artifacts)
        } else {
            $Artifacts = Join-Path $PSScriptRoot 'BenchmarkDotNet.Artifacts'
        }

        if (Test-Path -Path $Artifacts) {
            Remove-Item -Path $Artifacts -Recurse -Force -ErrorAction Stop
        }

        if ($Runtime) {
            ## Remove duplicate values.
            $hash = [ordered]@{}
            foreach ($item in $Runtime) {
                if (-not $hash.Contains($item)) {
                    $hash.Add($item, $null)
                }
            }
            $Runtime = $hash.Keys
        }
    }

    End {
        try {
            Push-Location -Path "$PSScriptRoot/benchmarks"
            $savedOFS = $OFS; $OFS = $null

            ## Aggregate BDN arguments.
            $runArgs = @(
                '--filter'
                foreach($entry in $Filter) { $entry }
                '--artifacts', $Artifacts
                '--envVars', 'POWERSHELL_TELEMETRY_OPTOUT:1'
                if ($List) { '--list', $List }
                if ($KeepFiles) { '--keepFiles' }
            )

            switch ($PSCmdlet.ParameterSetName) {
                'TargetPSVersion' {
                    Write-Log -message "Run benchmarks targeting '$TargetFramework' and the 'Microsoft.PowerShell.SDK' version '$TargetPSVersion' ..."
                    $env:PERF_TARGET_VERSION = $TargetPSVersion

                    ## Use 'Release' instead of 'release' (note the capital case) because BDN uses 'Release' when building the auto-generated
                    ## project, and MSBuild somehow recognizes 'release' and 'Release' as two different configurations and thus will rebuild
                    ## all dependencies unnecessarily.
                    dotnet run -c Release -f $TargetFramework $runArgs
                }

                'TargetFramework' {
                    if (-not $Quiet) {
                        $message = if ($TargetFramework -eq 'net11.0') { 'the current PowerShell code base ...' } else { "the corresponding latest version of 'Microsoft.PowerShell.SDK' ..." }
                        Write-Log -message "Run benchmarks targeting '$TargetFramework' and $message"
                    }

                    ## Use 'Release' instead of 'release' (note the capital case) because BDN uses 'Release' when building the auto-generated
                    ## project, and MSBuild somehow recognizes 'release' and 'Release' as two different configurations and thus will rebuild
                    ## all dependencies unnecessarily.
                    dotnet run -c Release -f $TargetFramework $runArgs
                }

                'Runtimes' {
                    Write-Log -message "Run benchmarks targeting multiple .NET runtimes: $Runtime ..."

                    ## Use 'Release' instead of 'release' (note the capital case) because BDN uses 'Release' when building the auto-generated
                    ## project, and MSBuild somehow recognizes 'release' and 'Release' as two different configurations and thus will rebuild
                    ## all dependencies unnecessarily.
                    dotnet run -c Release -f net11.0 --runtimes $Runtime $runArgs
                }
            }

            if (Test-Path $Artifacts) {
                Write-Log -message "`nBenchmark artifacts can be found at $Artifacts"
            }
        }
        finally {
            $OFS = $savedOFS
            $env:PERF_TARGET_VERSION = $null
            Pop-Location
        }
    }
}

function Compare-BenchmarkResult
{
    <#
    .SYNOPSIS
    Compare two benchmark run results to find possible regressions.

    When running benchmarks with 'Start-Benchmarking', you can define the result folder
    where to save the artifacts by specifying '-Artifacts'.

    To compare two benchmark run results, you need to specify the result folder paths
    for both runs, one as the base and one as the diff.

    .PARAMETER BaseResultPath
    Path to the benchmark result used as baseline.

    .PARAMETER DiffResultPath
    Path to the benchmark result to be compared with the baseline.

    .PARAMETER Threshold
    Threshold for Statistical Test. Examples: 5%, 10ms, 100ns, 1s

    .PARAMETER Noise
    Noise threshold for Statistical Test.
    The difference for 1.0ns and 1.1ns is 10%, but it's really just noise. Examples: 0.5ns 1ns.
    The default value is 0.3ns.

    .PARAMETER Top
    Filter the diff to top `N` results

    .EXAMPLE
    Import-Module ./test/perf/perf.psm1
    Compare-BenchmarkResult -BaseResultPath ./artifacts/ParameterBinding/baseline -DiffResultPath ./artifacts/ParameterBinding/current -Threshold 5%

    Compares the baseline and current parameter-binding benchmark results and reports statistically significant differences above the threshold.

    .EXAMPLE
    Import-Module ./test/perf/perf.psm1
    Compare-BenchmarkResult -BaseResultPath ./artifacts/ParameterBinding/baseline -DiffResultPath ./artifacts/ParameterBinding/current -Threshold 5% -Noise 0.5ns -Top 10

    Uses a 0.5 ns noise filter and shows at most the top 10 results.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $BaseResultPath,

        [Parameter(Mandatory)]
        [string] $DiffResultPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^\d{1,2}%$|^\d+(ms|ns|s)$')]
        [string] $Threshold,

        [ValidatePattern('^(\d\.)?\d+(ms|ns|s)$')]
        [string] $Noise,

        [ValidateRange(1, 100)]
        [int] $Top
    )

    Find-Dotnet

    try {
        Push-Location -Path "$PSScriptRoot/dotnet-tools/ResultsComparer"
        $savedOFS = $OFS; $OFS = $null

        $runArgs = @()
        if ($Noise) { $runArgs += "--noise $Noise" }
        if ($Top -gt 0) { $runArgs += "--top $Top" }

        dotnet run -c Release --base $BaseResultPath --diff $DiffResultPath --threshold $Threshold "$runArgs"
    }
    finally {
        $OFS = $savedOFS
        Pop-Location
    }
}

Export-ModuleMember -Function 'Start-Benchmarking', 'Compare-BenchmarkResult'
