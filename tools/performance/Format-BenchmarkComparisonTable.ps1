[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$BaseReportPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$DiffReportPath,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$BaseLabel = 'PS 7.6.0',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$DiffLabel = 'param_binding',

    [Parameter()]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-MeanToMicroseconds {
    param(
        [Parameter(Mandatory)]
        [string]$MeanText
    )

    $clean = ($MeanText -replace '[^0-9\.,]', '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return [double]::NaN
    }

    $numeric = [double]::Parse($clean.Replace('.', '').Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)

    if ($MeanText -match 'ms') {
        return $numeric * 1000
    }

    if ($MeanText -match 'ns') {
        return $numeric / 1000
    }

    return $numeric
}

function Convert-AllocatedToBytes {
    param(
        [Parameter(Mandatory)]
        [string]$AllocatedText
    )

    $clean = ($AllocatedText -replace '[^0-9\.,]', '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return [double]::NaN
    }

    $numeric = [double]::Parse($clean.Replace('.', '').Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)

    if ($AllocatedText -match 'MB') {
        return $numeric * 1MB
    }

    if ($AllocatedText -match 'KB') {
        return $numeric * 1KB
    }

    return $numeric
}

function Get-BenchmarkRows {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Benchmark report not found: $Path"
    }

    $rows = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch '^\|') {
            continue
        }

        if ($line -match '^\|\s*Method\s*\|' -or $line -match '^\|\s*[-:]+\s*\|') {
            continue
        }

        $parts = $line.Split('|') | ForEach-Object { $_.Trim() }
        $parts = $parts | Where-Object { $_ -ne '' }
        if ($parts.Count -lt 10) {
            continue
        }

        $method = $parts[0]
        $rows[$method] = [pscustomobject]@{
            Method = $method
            Mean = $parts[1]
            Error = $parts[2]
            StdDev = $parts[3]
            Allocated = $parts[9]
            MeanUs = Convert-MeanToMicroseconds -MeanText $parts[1]
            AllocBytes = Convert-AllocatedToBytes -AllocatedText $parts[9]
        }
    }

    return $rows
}

$baseRows = Get-BenchmarkRows -Path $BaseReportPath
$diffRows = Get-BenchmarkRows -Path $DiffReportPath

$methods = @($baseRows.Keys + $diffRows.Keys | Sort-Object -Unique)
if ($methods.Count -eq 0) {
    throw 'No benchmark rows were found in the provided reports.'
}

$builder = [System.Text.StringBuilder]::new()
$null = $builder.AppendLine('# Parameter Binding Benchmark Comparison')
$null = $builder.AppendLine()

foreach ($method in $methods) {
    if (-not $baseRows.ContainsKey($method) -or -not $diffRows.ContainsKey($method)) {
        continue
    }

    $base = $baseRows[$method]
    $diff = $diffRows[$method]

    $ratio = $diff.MeanUs / $base.MeanUs
    $deltaPct = ($ratio - 1.0) * 100.0
    $allocDeltaPct = (($diff.AllocBytes / $base.AllocBytes) - 1.0) * 100.0

    $null = $builder.AppendLine("## $method")
    $null = $builder.AppendLine("Mean Ratio ($DiffLabel/$BaseLabel): $($ratio.ToString('N3'))x ($($deltaPct.ToString('+0.00;-0.00'))%)")
    $null = $builder.AppendLine("Allocation Delta ($DiffLabel vs $BaseLabel): $($allocDeltaPct.ToString('+0.00;-0.00'))%")
    $null = $builder.AppendLine()
    $null = $builder.AppendLine('| Run | Mean | Error | StdDev | Allocated |')
    $null = $builder.AppendLine('|---|---:|---:|---:|---:|')
    $null = $builder.AppendLine("| $BaseLabel | $($base.Mean) | $($base.Error) | $($base.StdDev) | $($base.Allocated) |")
    $null = $builder.AppendLine("| $DiffLabel | $($diff.Mean) | $($diff.Error) | $($diff.StdDev) | $($diff.Allocated) |")
    $null = $builder.AppendLine()
}

$output = $builder.ToString().TrimEnd()

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Set-Content -LiteralPath $OutputPath -Value $output -Encoding utf8
}

$output
