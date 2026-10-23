# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Tests the isolated promotion cmdlet's cleanup and overlapping invocation behavior.
.DESCRIPTION
Run using the experimental pwsh executable. Uses real runspaces and a controlled
compilation barrier to force admission rejection, worker failure and cancellation.
Always releases the barrier and disposes runspaces; no WPR recording is started.
.EXAMPLE
./tmp/SelectString/regex-background/pwsh.exe -NoProfile -File ./test/SelectString/Test-PromotionLifecycle.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility
$diagnostics = 'Microsoft.PowerShell.Commands.SelectStringPromotionDiagnostics' -as [type]
if (!$diagnostics) { throw 'Use the isolated promotion build to run this test.' }
$oldMode = $env:SELECTSTRING_PROMOTION_MODE
$oldWarmInput = $env:SELECTSTRING_PROMOTION_WARM_INPUT
$env:SELECTSTRING_PROMOTION_MODE = 'background'

function Assert([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

# Hold conversion of the first input until compilation has actually entered the
# barrier. This avoids relying on scheduler timing for short pipelines.
Add-Type @'
using System;
using System.Threading;
public sealed class PromotionControlledInput
{
    public static ManualResetEventSlim Entered;
    public override string ToString()
    {
        if (!Entered.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Compiler did not enter barrier"); }
        return "hit";
    }
}
'@
[PromotionControlledInput]::Entered = $diagnostics::CompilationEntered

try {
    # The upstream conversion changes culture after BeginProcessing but before the
    # delayed compilation threshold. Both sides of promotion must keep Turkish I.
    Add-Type @'
public sealed class PromotionCultureInput
{
    public override string ToString()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return "\u0131nput";
    }
}
'@
    $previousCulture = [Globalization.CultureInfo]::CurrentCulture
    try {
        foreach ($mode in 'delayed-synchronous','background','delayed-background-warm') {
            $env:SELECTSTRING_PROMOTION_MODE = $mode
            $env:SELECTSTRING_PROMOTION_WARM_INPUT = 'INPUT'
            [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('tr-TR')
            $inputs = [object[]]::new(260)
            for ($i = 0; $i -lt $inputs.Count; $i++) { $inputs[$i] = [PromotionCultureInput]::new() }
            $output = @($inputs | Select-String '^(I)(.*)$')
            Assert ($output.Count -eq 260) "Construction culture lost in $mode"
        }
    }
    finally {
        [Globalization.CultureInfo]::CurrentCulture = $previousCulture
        $env:SELECTSTRING_PROMOTION_MODE = 'background'
    }
    foreach ($scenario in 'DownstreamStop','Fault','Cancellation') {
        $before = $diagnostics::Snapshot()
        $diagnostics::CompilationEntered.Reset()
        $gate = [Threading.ManualResetEventSlim]::new($false)
        $diagnostics::CompilationGate = $gate
        $diagnostics::FailCompilation = $scenario -eq 'Fault'
        $first = [PowerShell]::Create()
        $second = [PowerShell]::Create()
        $pending = $null
        try {
            if ($scenario -eq 'Cancellation') {
                $null = $first.AddScript({
                    & {
                        [PromotionControlledInput]::new()
                        for ($i = 0; $i -lt 1000000; $i++) { 'hit' }
                    } | Select-String '(?<word>hit)'
                })
            } else {
                $null = $first.AddScript({
                    [PromotionControlledInput]::new() | Select-String '(?<word>hit)' | Select-Object -First 1
                })
            }
            $pending = $first.BeginInvoke()
            Assert ($diagnostics::CompilationEntered.Wait(10000)) 'Compiler barrier was not reached'
            Assert (!$pending.IsCompleted) 'Invocation completed without draining its worker'

            $null = $second.AddScript({ 'hit' | Select-String '(?<word>hit)' })
            $output = $second.Invoke()
            Assert (!$second.HadErrors -and $output.Count -eq 1 -and $output[0].Matches[0].Groups['word'].Value -eq 'hit') 'Rejected invocation lost its result'
            $held = $diagnostics::Snapshot()
            Assert ($held[3] -eq $before[3] + 1 -and $held[7] -eq 1) 'Global admission limit was not enforced'

            if ($scenario -eq 'Cancellation') { $stop = $first.BeginStop($null, $null) }
            $gate.Set()
            Assert ($pending.AsyncWaitHandle.WaitOne(10000)) 'Invocation failed to complete after barrier release'
            if ($scenario -eq 'Cancellation') {
                $first.EndStop($stop)
                try { $null = $first.EndInvoke($pending) }
                catch [Management.Automation.PipelineStoppedException] { }
            } else {
                $output = $first.EndInvoke($pending)
                Assert (!$first.HadErrors -and $output.Count -eq 1 -and $output[0].Matches[0].Groups['word'].Value -eq 'hit') 'Speculation changed downstream-stop output'
            }
        }
        finally {
            $gate.Set()
            $first.Dispose()
            $second.Dispose()
            $diagnostics::CompilationGate = $null
            $diagnostics::FailCompilation = $false
            $gate.Dispose()
        }
        $after = $diagnostics::Snapshot()
        Assert ($after[0] -eq $before[0] + 2) 'Cmdlets were not disposed exactly once'
        Assert ($after[7] -eq 0) 'Worker survived invocation cleanup'
        $expectedFaults = $scenario -eq 'Fault' ? 1 : 0
        Assert ($after[5] -eq $before[5] + $expectedFaults) 'Worker failure was not observed'
        Write-Host "$scenario passed"
    }
}
finally {
    $env:SELECTSTRING_PROMOTION_MODE = $oldMode
    $env:SELECTSTRING_PROMOTION_WARM_INPUT = $oldWarmInput
}
Write-Host 'Promotion lifecycle checks passed.'
