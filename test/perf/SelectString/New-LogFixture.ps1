# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
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
