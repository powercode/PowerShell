# Compiled-regex experiment

Measured 2026-10-23. This is an explicit internal engine experiment; the cmdlet
still uses interpreted regexes. Both variants use the retained reader-buffer
policy. No compilation threshold or automatic selection policy is implemented.

## Method

BenchmarkDotNet 0.16.0-preview.2, .NET 11.0.0-rc.2.26504.105, Windows,
Intel Core i9-13900K, affinity mask 1, warm filesystem cache. Short jobs use one
launch, three warmup iterations and three measured iterations. These are
exploratory means, not a precise crossover estimate or shipping comparisons.

`ConstructAndScan` creates a new engine and new regexes on every timed operation.
Parsing, compilation and first-use JIT are included. `ReuseAndScan` reuses an
engine validated and warmed during setup. Both discard scan results through the
same counting callback. Single-string input is prepared outside timing. Log
generation and validation of named captures and result order are also untimed.
The large fixture is the deterministic 300 MiB application log used previously.

MemoryDiagnoser reports managed allocations; it does not measure all native
memory used by generated code. Engine timings exclude PowerShell binding and
output processing, and these runs do not measure time to first result.

## Initial large-log and single-string comparison

| Input | Operation | Interpreted mean | Compiled mean |
| --- | --- | ---: | ---: |
| Single string | Construct and match | 4.939 us | 2,099.009 us |
| Single string | Reuse and match | 223.69 ns | 71.75 ns |
| 300 MiB, all matches | Construct and scan | 467.530 ms | 280.706 ms |
| 300 MiB, all matches | Reuse and scan | 456.296 ms | 285.848 ms |
| 300 MiB, sparse matches | Construct and scan | 222.001 ms | 188.466 ms |
| 300 MiB, sparse matches | Reuse and scan | 237.080 ms | 187.578 ms |

Construction-inclusive means improve by about 40% for all matches and 15% for
sparse matches. For a single-string invocation, the absolute cost rises from
about 0.005 ms to 2.1 ms. That may be negligible interactively, but accumulates
across repeated separate invocations. The cmdlet constructs its engine once in
BeginProcessing, amortizing construction across all records/files in that
invocation; the single-string case does not represent a long input pipeline.
Some confidence intervals are wide; repeated end-to-end pipeline measurements
are required before choosing a policy.

Managed allocation for a fresh single-string operation rises from 14,072 to
27,880 bytes. Reused matching allocates 368 bytes in both cases. Large-file reused
scan allocations are identical between variants: 1,201,124,448 bytes for all
matches and 650,384,232 bytes for sparse matches. Compilation primarily targets
CPU work, not the allocation costs of reading and producing matches.

Raw artifacts: `tmp/SelectString/regex-initial/` and `regex-initial.log`.
All 12 cases completed. This run predates the equivalent refactor that assigns
regex options once using a tuple switch; the following sweep uses that refactor.

## Construction-inclusive size sweep

All values below are milliseconds per operation, including compilation and first
use. The single-string and 256-file controls have fixed inputs; each small file
contains approximately 4 KiB. Early exit returns the first match on line one.

| Input | Interpreted mean | Compiled mean |
| --- | ---: | ---: |
| 4 KiB, all matches | 0.101 | 2.444 |
| 4 KiB, sparse matches | 0.103 | 2.301 |
| 4 KiB, three patterns | 0.107 | 2.884 |
| 4 KiB, literal regex | 0.098 | 0.867 |
| 4 KiB, early exit | 0.097 | 2.302 |
| 256 small files | 23.786 | 26.465 |
| Single string | 0.004913 | 2.103 |
| 1 MiB, all matches | 1.510 | 3.666 |
| 1 MiB, sparse matches | 0.792 | 3.030 |
| 16 MiB, all matches | 25.147 | 18.315 |
| 16 MiB, sparse matches | 12.701 | 12.954 |
| 16 MiB, three patterns | 16.189 | 17.625 |
| 16 MiB, literal regex | 14.298 | 11.977 |
| 300 MiB, early exit | 0.088 | 2.320 |

At 16 MiB, all-match mean time falls by 27%, but sparse matching is approximately
tied and three patterns cost more. The literal case has a wide confidence interval
(interpreted mean 14.298 ms, error half-width 35.824 ms); do not infer a reliable
benefit there. The 256-file means suggest a regression but also have overlapping
confidence intervals. All 1 MiB and smaller single-file cases pay more for
compilation than they recover during matching.

Even a 300 MiB file is a poor compilation candidate when scanning stops after its
first line: this case is approximately 26 times slower. Unknown pipeline length,
downstream termination, pattern count, and match density further complicate any
selection policy. These results justify further controlled experiments, not a
fixed file-size threshold.

Raw artifacts: `tmp/SelectString/regex-sweep/` and `regex-sweep.log`, including
`source.json` with source hashes. All 28 cases completed; the tuple-switch
refactor is included. The benchmark command is documented in README.md.

## Validation and next decision

All 38 standalone engine tests pass, including interpreted/compiled capture
history, pattern precedence, Turkish case matching, case-sensitive matching and
invalid-pattern error attribution. The cmdlet has not been switched to compiled
regexes, so no experimental cmdlet build has been published yet.

Next: run repeated end-to-end comparisons against unchanged current and shipping builds. Include
compilation within each invocation, first-result latency, allocation and GC;
validate full cmdlet semantics before considering a production selection rule.
Cover many records feeding one invocation and actual downstream termination with
`Select-String ... | Select-Object -First 1`, including exception propagation and
cleanup. The engine early-exit benchmark does not measure those PowerShell costs.
