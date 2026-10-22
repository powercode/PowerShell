# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Build one revision in its own worktree. All compared revisions must use the
# same SDK/runtime and configuration. No generated files are copied across trees.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourceRoot,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Runtime = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Invoke-Dotnet {
    param([string[]] $ArgumentList)
    & dotnet @ArgumentList
    if ($LASTEXITCODE) { throw "dotnet failed ($LASTEXITCODE): $ArgumentList" }
}

Push-Location $SourceRoot
try {
    Push-Location src/ResGen
    try { Invoke-Dotnet @('run') } finally { Pop-Location }
    Invoke-Dotnet @('restore', 'src/Microsoft.PowerShell.SDK/Microsoft.PowerShell.SDK.csproj')
    Invoke-Dotnet @('msbuild', 'src/Microsoft.PowerShell.SDK/Microsoft.PowerShell.SDK.csproj', '/t:_GetDependencies', '/p:DesignTimeBuild=true', '/p:_DependencyFile=../TypeCatalogGen/powershell.inc', '/nologo')
    Push-Location src/TypeCatalogGen
    try { Invoke-Dotnet @('run', '--', '../System.Management.Automation/CoreCLR/CorePsTypeCatalog.cs', 'powershell.inc') } finally { Pop-Location }
    $project = if ($Runtime.StartsWith('win-')) { 'src/powershell-win-core/powershell-win-core.csproj' } else { 'src/powershell-unix/powershell-unix.csproj' }
    Invoke-Dotnet @('publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'false', '-o', $OutputDirectory, '-p:SatelliteResourceLanguages=en')
    $refDirectory = Join-Path $OutputDirectory 'ref'
    $null = New-Item -ItemType Directory -Path $refDirectory -Force
    Get-Content src/TypeCatalogGen/powershell.inc | Where-Object { $_ -like '*microsoft.netcore.app*' } | ForEach-Object {
        Copy-Item -LiteralPath $_.TrimEnd(';') -Destination $refDirectory -Force
    }
    [ordered]@{
        Commit = (& git rev-parse HEAD)
        Changes = @(& git status --short)
        SDK = (& dotnet --version)
        Runtime = $Runtime
        Configuration = 'Release'
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'comparison-build.json')
}
finally { Pop-Location }
