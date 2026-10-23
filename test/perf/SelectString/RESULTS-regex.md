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
invalid-pattern error attribution. Production cmdlet behavior remains interpreted;
the isolated compiled cmdlet build is validated below.

The end-to-end comparison below completes this follow-up. The engine early-exit
benchmark alone does not measure PowerShell exception propagation and cleanup.

## End-to-end pipeline and cmdlet comparison

All 72 cases completed in forward and reverse build order: shipping, unchanged
current (retained buffer policy), and an isolated compiled build. Each worker used
60 warmups, 25 measured invocations, affinity 1 and the same runtime as above.
Terminal support was false in every worker. Fixtures were warm-cache; capture
validation and first-result instrumentation were outside the timed samples.
The following ranges span the two run medians, not confidence intervals.

| Scenario | Shipping ms | Current ms | Compiled ms |
| --- | ---: | ---: | ---: |
| 300 MiB sparse | 297.20–301.82 | 224.96–250.03 | 207.41–208.74 |
| 300 MiB all matches | 512.16–528.19 | 438.06–442.57 | 335.84–356.46 |
| 1,000 records, all match | 3.41–3.52 | 3.38–3.75 | 4.92–5.07 |
| 1,000 records, 1% match | 2.96–2.97 | 2.93–2.96 | 4.68–4.68 |
| 100,000 records, all match | 74.97–95.67 | 74.23–75.42 | 65.64–67.06 |
| 100,000 records, 1% match | 55.10–55.16 | 57.77–64.83 | 53.76–57.31 |
| Record pipeline, downstream stop | 0.09–0.12 | 0.09–0.10 | 1.92–1.99 |
| File input, downstream stop | 0.16–0.17 | 0.18–0.18 | 1.03–1.13 |
| 256 small files | 8.99–9.37 | 9.28–9.45 | 9.98–10.34 |
| List, 256 files | 7.93–8.09 | 8.15–8.32 | 9.00–9.35 |
| Quiet, first-line match | 0.10–0.11 | 0.10–0.11 | 0.76–0.80 |
| Single string, literal regex | 0.06–0.06 | 0.07–0.07 | 0.72–0.75 |

PipelineCaptures sends prepared strings into ONE Select-String invocation with
named level/request/duration captures. Pattern construction in BeginProcessing is
included once per timed call, amortized across all inputs. PipelineStop feeds
10,000 records with 1% matching, starting with a match, and appends Select-Object
-First 1. FileStop uses the ordinary file fixture with a captured literal pattern
and the same downstream command. Both include actual termination and cleanup.
These patterns differ from the standalone log regex; startup costs need not agree.

Compared with current, compiled all-match log scans improve 19–23%, sparse logs
8–17%, and the dense 100,000-record pipeline 11–12% in corresponding run orders.
The sparse 100,000-record pipeline improves less than 1% in one order and 17% in
the other; shipping also outperforms current there. Treat that as variable evidence,
not a reliable compilation benefit. Shipping dense-pipeline time varied notably.

Actual downstream-stop overhead does not dwarf compilation in these workloads.
Compilation adds roughly 0.9 ms for FileStop and 1.8–1.9 ms for PipelineStop.
These absolute costs can be acceptable for occasional interactive invocations;
the ratio alone should not decide policy. Longer dense pipelines amortize setup.

First-result latency is one separately instrumented observation per worker, not
a repeated latency benchmark. For the dense 100,000-record pipeline, current was
1.72–1.74 ms and compiled 3.60–3.79 ms. Log all-match latency was 2.23–2.37 versus
4.69–5.26 ms; sparse was 2.30–2.35 versus 4.36–4.62 ms. Some other cases had large
outliers (current FileStop reached 11.53 ms), so do not infer latency distributions
or precise differences from this pass. Repeat latency sampling before a policy decision.

Managed allocation is almost unchanged for long scans: forward all-match means
were 1,371,259,791 bytes current versus 1,371,274,862 compiled. Sparse means were
650,605,692 versus 650,619,138 bytes. Reverse all-match totals across 25 samples
were 1,821/1/0 Gen0/Gen1/Gen2 collections current and 1,822/1/0 compiled. This
supports a CPU-work benefit rather than a material managed-allocation reduction.
Native generated-code memory remains outside these allocation measurements.

### Build provenance and validation

The isolated worktree at tmp/SelectString/regex-source is based on 4f1504d32.
Its only functional change passes compiledRegex: true when constructing the engine
in BeginProcessing. A local global_level setting resolves inherited analyzer
configuration conflicts from nesting the worktree; analyzers were not disabled.
Resources and types were regenerated for the Release build. The main cmdlet source
and unchanged current binary retain interpretation.

Utility assembly SHA256:

- Shipping: `12D591C8267549CFAD4616CB561C15380330E8F019C853F809A1AF3778910463`
- Current: `23A5A3233E8649C7EFB116E5445DC71A99FEED2586A129A6E5D3A67CAF9BACC0`
- Compiled: `D4B90FE12555EDF022106AD9D72B1907E8664193E233934F36AE4A41C5C22ACD`

The compiled build passes 56 Pester tests, 146 differential comparisons against
shipping, and the performance-script harness including the new pipeline cases.
All measured result counts agree; log and pipeline named captures were validated.

Raw results: tmp/SelectString/regex-endtoend-forward and regex-endtoend-reverse;
Run-RegexComparison.ps1 reproduces the matrix, Summarize-Regex.ps1 produces the
combined summary JSON. The isolated binary is tmp/SelectString/regex-compiled/pwsh.exe.
No ETW recording was needed for this comparison.

Next: evaluate a compilation policy using per-invocation work and acceptable
absolute startup cost. Repeat latency sampling and investigate variable sparse
pipeline results before choosing a threshold or changing production behavior.
