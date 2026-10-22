# Initial sequential extraction measurements

Measured 2026-10-23. Baseline: `17f1446e5c4d88636e0d65bfedbaf8fe1bd5aaa6`.
Candidate: the uncommitted sequential extraction on that baseline (source hashes below).
Both are framework-dependent Release builds with regenerated resources and type catalogs.
Only the shipped baseline and the new implementation are compared here.

## Correctness and build checks

- Utility build: no warnings or errors; full PowerShell publish succeeded.
- Standalone engine tests: 24 passed.
- Existing Select-String Pester tests: 56 passed, using Pester 4.10.1.
- Differential comparison: 128 cases passed, including public result APIs,
  captures, context, FileInfo transformations, warnings/errors, filtering and ordering.
- All 18 timed scenarios returned the same result counts in both builds.
  Counts supplement the differential tests; they do not replace them.

## End-to-end measurements

Windows 11, Intel Core i9-13900K (32 logical processors), x64,
.NET SDK `11.0.100-rc.2.26504.105`, runtime `11.0.0-rc.2.26504.105`.
Each scenario used a separate process, three warmups and seven measured calls.
The builds ran sequentially, with no other benchmark job launched alongside them.
Files were warm in the filesystem cache. Data generation and process startup were
excluded. These are exploratory measurements from one workstation, not a
statistically established speedup or regression threshold.

Default fixtures: 100,000 lines, 256 small files, and one 4 MiB line.
Time includes the command and Out-Null sink; allocation includes the PowerShell
harness. MiB means 1,048,576 bytes. Formatting is a separate context-rendering case.

| Scenario | Baseline ms | Candidate ms | Baseline allocated MiB | Candidate allocated MiB |
|---|---:|---:|---:|---:|
| String | 0.060 | 0.058 | 0.057 | 0.057 |
| Records | 27.696 | 26.366 | 10.908 | 10.916 |
| SmallFiles | 11.658 | 11.304 | 4.200 | 4.274 |
| LargeFile | 14.086 | 11.364 | 10.137 | 10.205 |
| LongLine | 5.264 | 5.098 | 8.088 | 8.088 |
| Dense | 80.206 | 92.917 | 100.794 | 107.661 |
| NoMatch | 11.731 | 8.469 | 9.189 | 9.190 |
| AllMatches | 60.136 | 62.209 | 126.046 | 132.913 |
| Simple | 112.712 | 107.178 | 9.907 | 9.975 |
| Context | 18.309 | 22.502 | 13.471 | 13.585 |
| Raw | 14.107 | 10.998 | 10.135 | 10.112 |
| List | 10.539 | 10.578 | 3.839 | 3.923 |
| QuietEarly | 0.133 | 0.114 | 0.064 | 0.064 |
| QuietLate | 8.775 | 6.151 | 4.662 | 4.662 |
| Utf16 | 15.496 | 12.536 | 10.158 | 10.226 |
| Utf32 | 28.716 | 26.021 | 10.158 | 10.226 |
| Legacy | 15.821 | 12.744 | 10.159 | 10.227 |
| Formatting | 32.584 | 35.014 | 17.875 | 17.990 |

The extraction is not allocation-neutral: the intermediate engine result adds
approximately 72 bytes per ordinary emitted match (about 6.9 MiB for 100,000
matches). Context additionally needs an intermediate context object. This is a
specific optimization target now that engine costs can be measured independently.
The no-match path does not allocate these result objects. Sparse matching has a
much smaller total allocation impact than dense matching.

First-result latency is measured in a separate pass with a counting sink, so it
includes instrumentation overhead. Peak working set includes process setup and
warmup and should not be interpreted as incremental search memory. Raw samples,
GC counts, latency, peak working set and binary hashes are in
[`bin/measurements/results.json`](bin/measurements/results.json).

## Standalone engine sample

BenchmarkDotNet `0.16.0-preview.2`, ShortRun (three warmups, three measured iterations),
same runtime. A 100,000-line file with two patterns and sparse matches:

| Matcher | Mean | Allocated per scan |
|---|---:|---:|
| Regex | 15.62 ms | 9.47 MiB |
| Culture-sensitive simple match | 317.19 ms | 9.24 MiB |

The short-run confidence intervals are wide; use a longer run before judging small
changes. The standalone workload has two patterns, while the command's Simple
scenario has one, so these timings are not directly comparable. Pattern setup has
a separate benchmark and is excluded from the scanning measurement. Full reports
are under [`bin/bdn/results`](bin/bdn/results).

## Next measurements

Preserve this correctness baseline while experimenting with eliminating the
intermediate result allocation, existence-only matching for eligible output modes,
and avoiding unnecessary regex result materialization. Measure single-record
startup separately from sustained scanning. Culture-aware simple matching needs
its own profile; replacing it with ordinal matching would change behavior.

Do not infer cold-disk or multi-gigabyte behavior from these default fixtures.
Scale `-LineCount` for large-file runs and track first-result latency and retained
memory. File-level concurrency and bounded ordered output remain later work.

## Candidate source fingerprints

- `SelectString.cs` SHA-256: `045849a54ad61485795c9cb547f2e6be3c4db7d7100a5e5329639cfbcb51c4e9`
- `SelectString.Engine.cs` SHA-256: `ee9b63523c1758cb1a8e5717969023e6b8cb6da4b94552cdbf08bda4b11a2460`
