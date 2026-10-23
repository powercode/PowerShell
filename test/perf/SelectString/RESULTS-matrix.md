# Full workload validation after concrete queues

Measured 2026-10-23 using the same saved Release executables as the previous
experiments: shipping source `17f1446e5`, first extraction (`41b3169b4`), and
concrete queues (`7ea3fccfe`). No engine code was changed during this validation.
The binaries were built before their corresponding commits; their build manifests
record the then-current parent commit and dirty files. Exact assembly hashes are
listed below.

## Method

Two complete matrices, 20 scenarios per build, ran sequentially in opposite build
order: shipping/extraction/queues, then queues/extraction/shipping. Each case used
a fresh process, 60 warmups, 25 timed calls, affinity mask 1, and default runtime
settings (no tiering/PGO overrides). All 120 cases succeeded and all result counts
agreed across builds and runs.

Machine: Intel Core i9-13900K, Windows x64, .NET 11.0.0-rc.2.26504.105. Fixtures:
100,000-line files, 256 small files, a 4 MiB line, and 10,000 streamed records.
Files were warm in the filesystem cache. Startup, fixture creation and warmup
were excluded from timing. Null redirection discarded output. A separate counting
pass measured first-result latency; tracing was disabled.

## Throughput

Values are ranges of the two process medians in milliseconds, not confidence
intervals. Formatting is a separate context-rendering scenario and includes its
ForEach-Object consumer. The remaining scenarios exclude formatting.

| Scenario | Shipping ms | Extraction ms | Queues ms | Results |
| --- | ---: | ---: | ---: | ---: |
| AllMatches | 75.57-76.53 | 46.88-47.77 | 40.57-41.74 | 100000 |
| Context | 15.96-16.68 | 20.11-20.52 | 11.85-11.94 | 1000 |
| Dense | 40.78-42.59 | 28.52-29.32 | 22.51-23.83 | 100000 |
| DenseContext | 88.97-95.86 | 52.76-53.24 | 32.25-33.05 | 100000 |
| Formatting | 29.82-30.01 | 32.29-32.53 | 25.00-25.56 | 1000 |
| LargeFile | 11.79-12.10 | 8.85-8.94 | 8.62-8.68 | 1000 |
| Legacy | 13.29-13.61 | 9.96-11.00 | 10.16-10.63 | 1000 |
| List | 7.65-7.86 | 8.10-8.13 | 7.96-8.07 | 256 |
| LongLine | 4.01-4.05 | 4.18-4.37 | 4.11-4.11 | 1 |
| NoMatch | 10.81-11.53 | 8.41-8.49 | 8.46-8.47 | 0 |
| NoMatchContext | 14.79-15.76 | 12.19-12.99 | 10.99-11.02 | 0 |
| QuietEarly | 0.09-0.10 | 0.09-0.11 | 0.09-0.09 | 1 |
| QuietLate | 8.54-8.92 | 6.06-6.10 | 6.26-6.41 | 1 |
| Raw | 11.73-11.75 | 8.71-8.77 | 8.35-8.67 | 1000 |
| Records | 25.59-26.92 | 25.82-25.95 | 26.33-26.48 | 100 |
| Simple | 98.05-103.11 | 100.50-101.85 | 105.11-107.06 | 1000 |
| SmallFiles | 7.96-8.63 | 8.61-8.94 | 7.98-8.67 | 256 |
| String | 0.05-0.06 | 0.06-0.06 | 0.06-0.06 | 1 |
| Utf16 | 13.03-13.38 | 9.92-10.12 | 10.17-10.26 | 1000 |
| Utf32 | 26.25-26.91 | 23.18-24.12 | 23.48-24.31 | 1000 |

Dense/context workloads improved in both runs. Sparse regex scans, Raw, late
Quiet and the encoding workloads also beat shipping. Small-file and streamed
record timings overlap across runs. A single record is too short for a useful
engine speedup claim from this end-to-end harness.

SimpleMatch was about 4-7% slower than shipping in these two runs. List was
about 3-4% slower. Longer follow-up runs below substantially reduced these gaps.
LongLine was about 1-3% slower, with essentially unchanged allocation. Some
workloads (including QuietLate and Utf16) also remain slightly slower than the
first extraction. This matrix does not establish an across-the-board speedup.

## Longer follow-up

Simple, List and String were repeated in fresh processes with 200 warmups and
100 measured calls, shipping/extraction/queues order. All nine cases succeeded
and counts agreed. Other settings were unchanged.

| Scenario | Shipping ms | Extraction ms | Queues ms |
| --- | ---: | ---: | ---: |
| Simple | 97.6480 | 97.6664 | 98.1428 |
| List | 7.5850 | 7.6032 | 7.6662 |
| String | 0.0647 | 0.0658 | 0.0669 |

Simple's gap fell to about 0.5% and List's to about 1.1%. String differed by
2.2 microseconds. These fresh-process results suggest warmup/runtime state
contributes to the earlier differences; they do not isolate its cause or prove
equivalence. Do not optimize culture-sensitive matching based solely on the
initial 4-7% gap. The next step is focused tracing of the new build, followed by
choosing a specific optimization from the remaining measured costs.

## Allocation, GC and latency

Approximate decimal MB allocated per invocation, including harness allocations:

| Scenario | Shipping | Extraction | Queues |
| --- | ---: | ---: | ---: |
| Dense | 59.28 | 66.48 | 55.28 |
| DenseContext | 85.69 | 97.69 | 74.49 |
| Context | 13.65 | 13.76 | 10.37 |
| NoMatchContext | 12.83 | 12.83 | 9.63 |
| Simple | 9.91 | 9.98 | 9.87 |

For DenseContext, total Gen0 collections across 25 timed calls were 114 for
shipping, 130 for extraction and 99 for queues in both runs. No Gen1/Gen2
collections occurred outside LongLine. LongLine had four Gen2 collections for
shipping/extraction and five for queues in each run; its large allocations need
separate investigation before changing buffer ownership or decoding.

First-result latency is one instrumented counting pass per case, not a latency
distribution. It includes the counting pipeline's initial overhead and can exceed
the complete throughput call for short scenarios. DenseContext first results
arrived in about 5.25-5.42 ms for queues and 5.46-5.75 ms for shipping; this does
not prove a latency improvement. No-match cases correctly have null latency and
zero outputs. Full values and GC counts are preserved in the local summaries.

Peak working set includes startup, warmup and counting, so it does not establish
incremental search memory or bounded memory at multi-gigabyte scale. Cold-storage
performance, larger files, concurrency and broader culture/pattern combinations
remain unmeasured. Existing 28 engine tests, 56 Pester tests and 138 differential
comparisons cover compatibility of this unchanged build; matching counts here
supplement that coverage.

## Artifacts and reproduction

Local, ignored artifacts:

- `tmp/SelectString/queues-matrix/results.json`
- `tmp/SelectString/queues-matrix-reverse/results.json`
- `tmp/SelectString/queues-matrix-followup/results.json` (200 warmups, 100 calls;
  `-Scenario Simple,List,String`)
- `tmp/SelectString/queues-matrix-summary.json` and `.csv` (both runs, including GC,
  latency, allocation, peak working set and binary hash)
- `tmp/SelectString/Summarize-Matrix.ps1` (checks statuses/counts and builds summaries)

Utility assembly SHA256:

- Shipping: `12D591C8267549CFAD4616CB561C15380330E8F019C853F809A1AF3778910463`
- Extraction: `2946A602E3FE1EEB4D81158653B3DC1D7AE9DDF3994651C8B3B5E6B23E3CD0D8`
- Queues: `15103D2D15FAAAF54A9787672B70F19DDE80651C21729CFBFF5C70CE16BC5B50`

Run from a PowerShell prompt at the repository root:

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/first-extraction/pwsh.exe,./tmp/SelectString/concrete-queues/pwsh.exe `
    -Revision shipping,extraction,queues `
    -Iterations 25 -WarmupIterations 60 -Affinity 1 -TimeoutSeconds 180 `
    -Artifacts ./tmp/SelectString/queues-matrix
```

Reverse executable/revision order and use a separate output directory for the
second run. Counting is deliberately enabled here. Use `-SkipCounting` for CPU
captures, and select measured regions. The old `candidate` executable does not
contain the concrete queue change.
