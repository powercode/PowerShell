# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Verifies asynchronous Select-String pipeline stop and file-handle cleanup.
.DESCRIPTION
Run with the candidate pwsh executable. Holds the downstream consumer after its
first result, requests pipeline stop from another thread, then releases the
consumer. Uses events rather than sleeps; timeouts only guard against deadlocks.
Temporary input files are removed after all workers have stopped.
.EXAMPLE
./tmp/SelectString/parallel-cmdlet/pwsh.exe -NoProfile -File ./test/SelectString/Test-ParallelStop.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('SelectString-stop-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($directory)
$arrived = [Threading.ManualResetEventSlim]::new()
$release = [Threading.ManualResetEventSlim]::new()
$pipeline = [PowerShell]::Create()
try {
    $paths = @('first.txt', 'second.txt', 'third.txt') | ForEach-Object { Join-Path $directory $_ }
    $lines = [string[]]::new(10000)
    [Array]::Fill($lines, 'hit')
    foreach ($path in $paths) {
        [IO.File]::WriteAllLines($path, $lines)
    }

    $null = $pipeline.AddScript({
        param($paths, $arrived, $release)
        Select-String -LiteralPath $paths -Pattern hit | ForEach-Object {
            $arrived.Set()
            if (!$release.Wait([TimeSpan]::FromSeconds(15))) {
                throw 'Consumer was not released.'
            }
            $_
        }
    }).AddArgument($paths).AddArgument($arrived).AddArgument($release)
    $invocation = $pipeline.BeginInvoke()
    if (!$arrived.Wait([TimeSpan]::FromSeconds(15))) {
        throw 'The pipeline did not deliver its first result.'
    }

    $stopping = $pipeline.BeginStop($null, $null)
    $release.Set()
    if (!$stopping.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(15))) {
        throw 'Pipeline stop did not complete.'
    }
    $pipeline.EndStop($stopping)
    try {
        $null = $pipeline.EndInvoke($invocation)
    }
    catch [Management.Automation.PipelineStoppedException] {
        # Expected: asynchronous stop terminates this invocation.
    }
    if ($pipeline.InvocationStateInfo.State -ne 'Stopped') {
        throw "Unexpected pipeline state: $($pipeline.InvocationStateInfo.State)"
    }

    foreach ($path in $paths) {
        $handle = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $handle.Dispose()
    }
    'Passed asynchronous pipeline stop and exclusive file reopen checks.'
}
finally {
    $release.Set()
    $pipeline.Dispose()
    $arrived.Dispose()
    $release.Dispose()
    [IO.Directory]::Delete($directory, $true)
}
