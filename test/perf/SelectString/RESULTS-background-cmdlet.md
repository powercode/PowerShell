# Isolated cmdlet background-compilation experiment

Measured 2026-10-23. Production sources on the main branch are unchanged. The
isolated build adds a matching delegate used by both records and file scans,
retains existing context/output state, and implements IDisposable cleanup plus
cooperative stop notification. Sources and a reproducible patch are under
`test/perf/SelectString/experiment/`; build instructions are in README.md.

## Compatibility and lifecycle

The final experimental binary passes 146 shipping comparisons and 56 Pester tests.
The independent engine/prototype suite passes 57 tests. Deterministic runspace
checks hold a compiler behind a barrier and verify that another invocation falls
back without queuing, returns correct captures, and completes. Downstream
Select-Object -First 1, injected worker failure and explicit cancellation each
leave no worker behind and dispose both cmdlets exactly once. Separate tests
change culture from Turkish to English after BeginProcessing and verify retention
across delayed synchronous and background promotion. The barrier and fault hooks
are disabled during measurements.

The experiment joins admitted work in Dispose. This makes cleanup cost visible;
it does not establish that joined cleanup is the final production policy. Work
already compiling cannot be forcibly cancelled. Stop notification suppresses
subsequent speculative admission/publication; matching still follows PowerShell's
existing stopping path. Worker faults retain interpreted output and are counted;
any fault during a benchmark fails that measurement.

## Method

Compare shipping/current binaries against six modes in one experimental binary:
interpreted, compiled, background constructor-only, background with worker first
execution, delayed synchronous, and delayed background first execution. The two
delayed modes use the exploratory 128-record gate. First execution uses the chosen
authored fixture input, never an arbitrary warmup for production patterns.

The same-binary interpreted control exposes the hook/environment-selection overhead.
The compiled control is also from this binary; the earlier always-compiled binary
is preserved separately. All runs retain the reader-buffer policy. Each worker
uses 30 warmups, 15 timed invocations and five separate latency calls, with output
redirected to null and terminal highlighting disabled. Setup, fixture validation
and latency counting are outside throughput timing. Disposal is inside timing.

The focused matrix covers 10,000 literal matches and long wildcard near misses in
files and record pipelines, plus real file/record downstream termination. Both
variant and affinity orders are reversed for the repeat. Affinity 1 pins one
logical CPU; affinity 5 selects CPUs 0 and 2 on distinct physical cores. Windows
reports core masks 3 and 12 for those cores. The topology snapshot and script are
in `tmp/SelectString/promotion-topology.json` and `Get-PromotionTopology.ps1`.
The mapping uses [Windows processor-core relationships](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-system_logical_processor_information).

CPU is recorded across the whole measurement loop, including harness bookkeeping.
Promotion counters cover timed invocations only: admitted/started/rejected/promoted,
faults, drain time and remaining workers. Short CPU windows are still coarse.
These are exploratory median ranges, not confidence intervals or a randomized study.

Raw final-binary runs: `tmp/SelectString/promotion-cmdlet2-forward-1`,
`promotion-cmdlet2-forward-5`, `promotion-cmdlet2-reverse-5` and
`promotion-cmdlet2-reverse-1`. Source/binary hashes are in
`promotion-cmdlet-source.json`; the measured harness is preserved as
`promotion-measured-harness.psm1`. `Run-PromotionCmdlet.ps1` and
`Summarize-PromotionCmdlet.ps1` reproduce/aggregate the run. The interrupted
`promotion-cmdlet-forward-*` pilot is excluded; it predates the delayed-control
culture correction. All comparisons below use the corrected binary.

## Long wildcard near misses

Milliseconds per invocation, range of the two run medians:
| Variant | File, one CPU | File, two cores | Records, one CPU | Records, two cores |
| --- | ---: | ---: | ---: | ---: |
| shipping | 222.296-229.292 | 218.976-225.909 | 204.571-209.270 | 201.639-202.445 |
| current | 211.835-221.916 | 209.608-215.858 | 201.711-211.174 | 205.548-206.295 |
| interpreted | 208.673-214.738 | 211.809-212.877 | 204.879-206.929 | 203.205-203.377 |
| compiled | 20.417-21.845 | 19.563-22.100 | 31.923-33.261 | 10.426-10.809 |
| background | 33.361-33.792 | 24.438-26.112 | 34.797-35.071 | 10.720-10.883 |
| background-warm | 32.686-33.761 | 19.574-21.477 | 35.870-36.336 | 10.832-30.452 |
| delayed-synchronous | 24.703-26.154 | 32.750-33.306 | 37.552-37.792 | 21.838-22.031 |
| delayed-background-warm | 35.471-35.516 | 20.971-31.081 | 46.863-61.038 | 21.869-21.969 |

Background constructor-only is consistently much faster than interpretation on
these expensive inputs, approaching compiled record throughput with two cores.
The worker-warm and delayed variants vary more; their results do not identify a
universal best mode. One-CPU scheduling costs remain visible. These observations
are specific to the long failure path, not evidence to compile every wildcard.

## Downstream termination

Total invocation time includes PowerShell's stop exception/cleanup and worker drain:
| Variant | Pipeline stop, one CPU | Pipeline stop, two cores | File stop, one CPU | File stop, two cores |
| --- | ---: | ---: | ---: | ---: |
| shipping | 0.112-0.131 | 0.115-0.147 | 0.189-0.192 | 0.183-0.222 |
| current | 0.115-0.120 | 0.152-0.165 | 0.191-0.194 | 0.209-0.222 |
| interpreted | 0.115-0.144 | 0.169-0.172 | 0.193-0.196 | 0.204-0.231 |
| compiled | 1.930-1.992 | 1.932-1.988 | 1.006-1.049 | 1.013-1.031 |
| background | 0.134-0.156 | 0.219-0.273 | 0.224-0.229 | 0.342-0.974 |
| background-warm | 0.131-0.131 | 0.217-0.241 | 0.228-0.230 | 0.320-0.884 |
| delayed-synchronous | 0.112-0.123 | 0.127-0.155 | 0.195-0.207 | 0.212-0.230 |
| delayed-background-warm | 0.113-0.115 | 0.119-0.139 | 0.198-0.204 | 0.212-0.221 |

On two cores, constructor-only file-stop first-result medians were 1.01-1.05 ms,
versus 0.22-0.26 ms for the same-binary interpreted control. A replacement can be
ready before the first match but still perform its first-use JIT on the foreground.
The warm variant kept first-result medians around 0.26-0.27 ms, yet total medians
ranged 0.32-0.88 ms. In these two runs it started 3 or 10 compilations during 15
timed calls, promoted none, and spent a mean 0.20 or 0.47 ms per call draining.
Latency counting is a separate instrumented pass, so its medians are not paired
with individual throughput samples. The 128-record gate admitted no work for these
early-exit cases and remained close to interpreted total time.

This demonstrates why a foreground stopwatch alone is insufficient: quick output
can coexist with wasted background compilation and delayed disposal. Absolute
costs are sub-millisecond for many cases, but they are not automatically dwarfed
by PowerShell termination overhead.

## Cheap pipeline follow-up

The initial 30-warmup literal-record cases varied considerably, so a separate
same-binary follow-up used 200 warmups, 100 measured calls, 25 latency calls and
reversed mode order on the verified two-core mask. All eight cases validated.
Raw files: `tmp/SelectString/promotion-literal-steady-forward` and `-reverse`;
aggregate: `promotion-literal-steady-summary.json`; runner: `Run-PromotionLiteralSteady.ps1`.

| Mode | Wall median range ms | First-result median range ms | CPU mean range ms/call |
| --- | ---: | ---: | ---: |
| Interpreted | 5.73-5.79 | 0.08-0.09 | 7.66-7.97 |
| Compiled | 6.27-6.51 | 0.62-0.67 | 7.19-9.53 |
| Background constructor | 6.37-6.60 | 0.15-0.20 | 7.03-7.66 |
| Background warm | 6.05-6.19 | 0.16-0.20 | 7.34-8.44 |

The longer-warmup observations are substantially closer across runs, but are not
proof of the cause of the initial variation. They provide no throughput reason to
compile this literal workload. CPU includes the harness loop and runtime threads;
the overlapping ranges do not support small CPU-efficiency claims.

## Status and next work

All 192 main cases and eight longer-warmup cases passed count/capture checks. Binary
hashes were consistent per variant, terminal capability was false, and no timed
worker faulted or remained after cleanup. Promotion adapters completed exactly
once per timed invocation. No WPR recordings were started.

The integration is viable as an experiment, not a production policy. Next broaden
the matrix to large-log captures, sparse/dense records, many small files, context,
Quiet/List/Raw and multiple patterns. Use adequate warmup/CPU windows, then evaluate
admission based on actual work and pattern evidence. Keep delayed synchronous
compilation as a control. Revisit joined cleanup versus bounded abandoned work
explicitly before any production proposal; speculative warmup remains fixture-only.
