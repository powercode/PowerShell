# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [string] $Baseline,
    [string] $Candidate,
    [string] $Artifacts = (Join-Path $PSScriptRoot 'bin/comparison'),
    [switch] $Worker,
    [string] $FixtureRoot,
    [string] $OutputFile
)

$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!$Baseline -or !$Candidate) { throw 'Specify both -Baseline and -Candidate pwsh executables.' }
    $Artifacts = [IO.Path]::GetFullPath($Artifacts)
    $null = New-Item -ItemType Directory -Path $Artifacts -Force
    $FixtureRoot = Join-Path $Artifacts 'fixtures'
    $null = New-Item -ItemType Directory -Path $FixtureRoot -Force
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'one.txt'), "zero`nhit aa`nhit ab`nthree`nlast hit")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'two.txt'), "hit new`nend")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'none.txt'), "zero`none")
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'literal[1].txt'), 'hit literal')
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'utf16.txt'), "é`r`nhit café`rfinal", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllBytes((Join-Path $FixtureRoot 'malformed.txt'), [byte[]](0x68,0x69,0x74,0xff,0x0a))
    foreach ($entry in @(@{ Name = 'baseline'; Exe = $Baseline }, @{ Name = 'candidate'; Exe = $Candidate })) {
        & $entry.Exe -NoLogo -NoProfile -File $PSCommandPath -Worker -FixtureRoot $FixtureRoot -OutputFile (Join-Path $Artifacts ($entry.Name + '.json'))
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
Add-Case 'downstream-stop' { Select-String -LiteralPath $one -Pattern hit | Select-Object -First 1 }
Add-Case 'list-file-closed' {
    Select-String -LiteralPath $one -Pattern hit -List | ForEach-Object {
        $stream = [IO.File]::Open($one, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Dispose()
        $_
    }
}
$snapshot | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputFile -Encoding utf8
