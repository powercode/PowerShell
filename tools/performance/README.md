
# PowerShell Performance Analysis

This directory contains useful scripts and related files for analyzing PowerShell performance.

If you use the [Windows Performance Toolkit](https://learn.microsoft.com/windows-hardware/test/wpt/), you can use the following to collect data and analyze a trace.

```PowerShell
$PowerShellGitRepo = "D:\PowerShell"
wpr -start $PowerShellGitRepo\tools\performance\PowerShell.wprp -filemode
pwsh.exe -NoProfile -Command "echo 1"
wpr -stop PowerShellTrace.etl
wpa -i wpa://.\PowerShellTrace.etl?profile=$PowerShellGitRepo\tools\performance\PowerShell.wpaProfile
```

When wpa opens, under System Activity, you'll find a section "Regions of Interest".
With the above wpaProfile, you should see a bunch of PowerShell related regions as well as GC and JIT activity.

If you use [PerfView](https://github.com/microsoft/perfview), you can collect a trace by running

```PowerShell
Invoke-PerfviewPS.ps1 -scenario { echo 1 }
perfview .\perfviewdata.etl
```

The etl files collected with perfview or wpr should contain roughly the same events.

Also note that you can collect the trace with one tool and analyze with the other.

## Select-String engine analysis

`SelectString.wpaProfile` derives from the general profile, puts sampled CPU
stacks first, and uses `SelectString.stacktags` instead of script-focused tags
and regions. It retains the shared GC/JIT regions. Process, thread, CPU, module,
function and stack-tag columns help separate engine work from runtime and pipeline
costs. Open a captured trace with:

```powershell
$profile = Join-Path $PowerShellGitRepo 'tools/performance/SelectString.wpaProfile'
wpa.exe -i C:\Traces\candidate-DenseContext.etl -profile $profile
```

Use the focused capture in `test/perf/SelectString/New-EtwCapture.ps1`. Filter to
the benchmark worker PID and the same time interval in all tables. Compare CPU
sample weights, GC pauses and JIT activity separately; inclusive stack categories
are not additive exclusive costs. Tags cover shipping and extracted engine names,
but inlining or unresolved symbols can hide frames. Verify tag matches against
the raw stacks before using them for attribution. JIT regions accept completion
event versions 1 and 2; validate pairing in a real trace. Region duration across
threads is not total JIT CPU time.

The focused capture does not collect context switches or disk I/O. Sampled CPU
does not measure blocked time; use the general capture profile if those become
the next question. The analysis profile and tags need validation in WPA against
the collected trace; XML validation alone does not establish correct attribution.

## Symbols

PDB files are not published for PowerShell Core,
so the current recommendation is to build PowerShell yourself passing `-CrossGen` to `Start-Build`.

If profiling Windows PowerShell, symbols are generated from GAC.
wprui.exe and perfview.exe will both generate the PDB files needed.

## Files

| File | Description |
| ---- | ----------- |
| GC.Regions.xml | WPA regions of interest for GC |
| JIT.Regions.xml | WPA regions of interest for JIT |
| PowerShell.Regions.xml | WPA regions of interest for PowerShell |
| PowerShell.stacktags | PowerShell stack tags |
| PowerShell.wpaProfile | WPA profile to load regions of interest and stack tags |
| PowerShell.wprp | WPR profile to enable CLR and PowerShell ETW events |
| Invoke-PerfviewPS.ps1 | Script to run perfview and with PowerShell ETW events enabled |

