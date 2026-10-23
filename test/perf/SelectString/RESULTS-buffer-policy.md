# File-size selection and end-to-end buffer validation

Measured 2026-10-23. The candidate uses 16 KiB for seekable files of at least
1 MiB with runtime-provided UTF-8 replacement decoding, except List/Quiet scans.
All other cases retain 1 KiB. BOM detection still overrides the requested encoding.
Strict decoders, custom encoding types and custom fallbacks keep their original
read-ahead behavior. The standalone benchmark can explicitly override selection.

## Selection evidence

The file-size sweep used one launch, three warmups and three measured iterations
per case, affinity 1. Means below are exploratory, not precise crossover estimates.
Files contain complete generated log lines and can slightly exceed the target size.
Each setup validates named captures and result order outside timing.

| Target size | Density | 1 KiB buffer | 16 KiB buffer | 64 KiB buffer |
| --- | --- | ---: | ---: | ---: |
| 4 KiB | All | 94.03 us | 94.22 us | 111.40 us |
| 4 KiB | Sparse | 89.43 us | 91.33 us | 110.03 us |
| 64 KiB | All | 198.25 us | 182.43 us | 226.95 us |
| 64 KiB | Sparse | 149.46 us | 134.61 us | 171.69 us |
| 1 MiB | All | 1.844 ms | 1.478 ms | 1.487 ms |
| 1 MiB | Sparse | 1.066 ms | 0.745 ms | 0.729 ms |
| 16 MiB | All | 29.886 ms | 23.657 ms | 22.313 ms |
| 16 MiB | Sparse | 18.023 ms | 12.199 ms | 10.911 ms |

At 4 KiB, 16 KiB offers no useful speedup. At 1 MiB it reduces mean time by
about 20%/30%, with approximately 41 KiB additional allocation per opened file.
64 KiB adds approximately 185 KiB instead and recorded Gen2 collections even
on small inputs. The 1 MiB gate is conservative; it is not a universal optimum.
Earlier 300 MiB confirmation is in RESULTS-buffers.md.

## End-to-end method

Shipping is 17f1446e5. Control is a fresh Release build of 3e0c8c4ba with the
fixed 1 KiB reader. Candidate differs in the selection policy. Both new builds
regenerated resources and the type catalog. All three use the same runtime and
measurement module, discard output via null redirection, and run without ETW.

Windows 11 / Intel Core i9-13900K; .NET 11.0.0-rc.2.26504.105. Affinity mask 1,
60 warmups and 25 measured calls per fresh worker, warm filesystem cache.
HostSupportsVirtualTerminal was false in every worker. Fixture generation,
validation and warmup are excluded from wall time. Counting and first-result
latency use a separate pass after measurement.

Each matrix covers 13 scenarios, first shipping/control/candidate, then reverse
build order. All 78 cases completed with matching result counts; log validation
also checks named captures. Table entries are ranges of the two run medians,
not confidence intervals. These tests do not establish cold-cache or cross-platform
performance.

An earlier 15-warmup/15-measurement trial was not steady: candidate sparse samples
fell from roughly 440 ms to 280 ms near the end. It was retained as exploratory
raw data and superseded by the 60/25 runs below. Runtime settling is a possible
explanation, not a proven attribution.

## Wall time

| Scenario | Shipping ms | Control ms | Candidate ms |
| --- | ---: | ---: | ---: |
| LogSparse | 292.541-298.603 | 295.039-295.695 | 213.767-231.452 |
| LogAllMatches | 514.020-519.801 | 500.447-502.412 | 415.828-436.497 |
| SmallFiles | 8.477-8.595 | 9.008-9.487 | 8.599-9.355 |
| QuietEarly | 0.102-0.102 | 0.105-0.117 | 0.115-0.117 |
| QuietLate | 8.665-9.048 | 6.296-6.313 | 6.268-6.438 |
| List | 7.614-7.931 | 8.062-8.177 | 7.302-8.140 |
| String | 0.064-0.077 | 0.063-0.064 | 0.062-0.065 |
| Records | 26.386-27.046 | 27.213-27.355 | 26.834-27.528 |
| DenseContext | 63.609-64.079 | 32.084-33.163 | 32.141-32.572 |
| Utf16 | 12.871-13.215 | 10.068-10.190 | 8.586-8.673 |
| Utf32 | 26.797-27.044 | 23.486-24.307 | 19.957-20.265 |
| Legacy | 13.359-13.650 | 10.356-10.528 | 10.363-10.501 |
| LongLine | 4.266-4.368 | 4.373-4.533 | 3.548-3.622 |

Changes in large-log median time relative to control, separately by run:

- forward, LogSparse: -27.7%.
- forward, LogAllMatches: -16.9%.
- reverse, LogSparse: -21.6%.
- reverse, LogAllMatches: -13.1%.

## Allocations and streaming

Allocated bytes per invocation are process-wide managed allocation deltas.
First-result latency is measured separately and is noisier than whole-scan timing.

| Scenario | Control allocated bytes | Candidate allocated bytes | Control first result ms | Candidate first result ms |
| --- | ---: | ---: | ---: | ---: |
| LogSparse | 650563216.000-650563224.960 | 650605339.840-650605393.280 | 2.105-2.368 | 2.463-2.466 |
| LogAllMatches | 1371211121.280-1371211145.920 | 1371253064.000-1371259391.040 | 2.209-2.238 | 2.206-2.438 |
| SmallFiles | 4226472.640-4226514.240 | 4226470.720-4227024.000 | 8.774-9.356 | 7.143-7.961 |
| QuietEarly | 46491.840-46508.160 | 46507.200-46530.880 | 4.041-4.342 | 3.975-4.332 |
| List | 3858250.240-3858290.880 | 3858230.400-3858262.400 | 7.455-8.192 | 7.572-7.942 |

## Decision

Retain the 16 KiB policy: both build orders show substantial large-log gains
against the unchanged control, without a repeatable whole-scan regression in the
small-file or early-exit workloads. SmallFiles still trails shipping in part of
this measurement range; this policy does not resolve every pre-existing difference.

Sparse first-result latency in the separate instrumented counting pass was about
0.10-0.36 ms higher than control. This is a latency tradeoff to monitor, not a claim
of improvement in every metric. Full-scan allocations increased by about 41 KiB
per large file, while small-file and early-exit allocation costs stayed comparable.

The candidate executable is `tmp/SelectString/candidate/pwsh.exe`; the fixed-buffer
control is `tmp/SelectString/buffer-control/pwsh.exe`. The diagnostic capture below
confirms the large-log improvement against shipping. Compiled regex
remains a separate experiment with this buffer policy held constant in both arms.

## ETW confirmation: 20261023-133709

All four traces have zero lost events and buffers. Each contains 25 paired measured
iterations after 60 warmups, with affinity 1, null redirection and no counting
phase. Both hosts report virtual-terminal support. Assembly hashes match those
below; the same validated 300 MiB fixture yielded 925 sparse results/matches and
924,366 all-match results containing 978,741 matches.

| Scenario | Shipping median ms | Candidate median ms | Wall-time reduction | Shipping MB allocated/call | Candidate MB allocated/call |
| --- | ---: | ---: | ---: | ---: | ---: |
| LogSparse | 328.28 | 227.75 | 30.6% | 709.83 | 650.74 |
| LogAllMatches | 537.69 | 461.64 | 14.1% | 1541.30 | 1504.36 |

Allocations use decimal MB. Summed stopwatch times across all 25 calls fall from
8670.96 to 6170.64 ms for Sparse (28.8%) and 13940.28 to 11852.93 ms for AllMatches
(15.0%). Individual calls still have spikes; these are diagnostic traced runs,
not replacements for the repeated untraced control comparisons above. Shipping
versus candidate includes earlier engine changes as well as the buffer policy.

Measured-region CPU leaf samples support reduced copying and native overhead:

| Sample category | Sparse shipping / candidate | AllMatches shipping / candidate |
| --- | ---: | ---: |
| Total CPU samples | 8476 / 5933 | 13650 / 11591 |
| ntoskrnl.exe | 2760 / 1469 | 2767 / 1656 |
| SpanHelpers.Memmove | 516 / 232 | 582 / 277 |
| RegexInterpreter.TryMatchAtCurrentPosition | 482 / 460 | 2462 / 2462 |
| Utf8Utility.TranscodeToUtf16 | 397 / 516 | 366 / 527 |

Decoding samples increase rather than decrease; this is not evidence that every
reader-related operation became faster. Kernel leaf samples also include work
other than file I/O. Managed method mapping uses JIT address ranges and is
heuristic; inlining and unresolved symbols limit attribution.

GC suspension totals are 266.74 / 275.78 ms for Sparse and 625.94 / 610.29 ms for
AllMatches (shipping / candidate). Thus reduced GC pauses do not explain the
wall-time gains. No Gen2 collections occurred. AllMatches still spends substantial
CPU in interpreted matching and MatchCollection.GetMatch, supporting the planned
compiled-regex experiment, including construction and first-use costs.

Raw captures: `tmp/SelectString/traces/20261023-133709/<scenario>/`.
Local exports, loss statistics, analysis scripts, logs and summaries:
`%TEMP%/SelectString-133709-analysis/<scenario>/`. Analyses select individual
measured marker pairs, excluding warmup and gaps. Dumper schema warnings are
distinct from event loss.

## Compatibility and provenance

35 standalone tests, 56 existing Pester tests and 146 shipping differential
comparisons passed. New comparisons exercise large UTF-8 files, BOM overrides,
context/captures, Raw/List/Quiet and strict-decoder downstream early termination.
A custom UTF-8 subclass test verifies that the engine does not enlarge its buffer.
Pester needed its usual temporary registry access outside the sandbox.

Raw ignored artifacts: `tmp/SelectString/buffer-sizes`,
`buffer-policy-forward` / `buffer-policy-reverse` (exploratory),
`buffer-policy-steady-forward` / `buffer-policy-steady-reverse` (reported),
`buffer-policy-comparison`, and the build/test logs under `tmp/SelectString`.
The two new executable directories contain `comparison-build.json` provenance.

Utility assembly SHA256:

- shipping: `12D591C8267549CFAD4616CB561C15380330E8F019C853F809A1AF3778910463`
- control: `ADD97AF37BD9D533212FC4B1E6610B859E194906C08679913067FCE91C69C077`
- candidate: `23A5A3233E8649C7EFB116E5445DC71A99FEED2586A129A6E5D3A67CAF9BACC0`

Candidate engine source SHA256:

`5FCAB327D8B6CD03C74892DF09F647FB233BE73044FBE5127ECE740DFC351005`
