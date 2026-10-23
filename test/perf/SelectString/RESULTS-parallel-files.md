# Ordered file parallelism: first end-to-end measurements

Measured 2026-10-24. Two file workers do not yet provide a general throughput win
over the compiled sequential control. Dense context regresses substantially;
the no-match control is about 1.9 times faster. Increasing worker count is not
the next step until the output cost is understood.

## Method

Compared shipping (`baseline`), compiled sequential (`parallel-sequential`), and
the current two-worker cmdlet (`parallel-cmdlet`, source checkpoint `7964e2da4`).
The sequential control already compiles regexes once per invocation: improvements
over shipping alone must not be attributed to parallelism.

Windows 10.0.26300, .NET 11.0.0-rc.2.26504.105, Intel family 6/model 183,
32 logical processors. Affinity unrestricted. Warm caches, three warmups, five
timed invocations per worker, fresh processes per build/workload. Repeated in
forward and reverse build order. The table gives the range of those two medians,
not a confidence interval. The harness did not request ETW capture.

All cases use `PatternSmallFiles`, `PatternFamily SqlDuration`, one pattern,
case-insensitive matching. Validation checks output order, line numbers, captures
and context before timing. The first-result pass uses a separate counting sink;
it is one sample per process and is not a latency distribution.

| Workload | Records / lines per file | Input | Output |
| --- | ---: | --- | --- |
| Many small files | 16,384 / 16 | Match | Matches |
| Four larger files | 262,144 / 65,536 | Mixed | Matches |
| Four files, no matches | 32,768 / 8,192 | LongNearMiss | No results |
| Dense context | 262,144 / 65,536 | Match | Context 2 |
| Slow consumer | 8,192 / 2,048 | Match | Matches; sleep 1 ms every 128 results |

Mixed inputs include long near misses, totaling roughly 260 MiB across four
files. They are a synthetic regex stress case, not a representative production
log distribution. The no-match control contains roughly 128 MiB of long near
misses. Dense-context input is much smaller in bytes but emits 262,144
results. Fixture generation and validation are outside timing.

## Delivered wall time

| Workload | Shipping ms | Compiled sequential ms | Parallel two ms |
| --- | ---: | ---: | ---: |
| Many small files | 41.24–42.94 | 43.81–48.19 | 40.43–46.74 |
| Four larger files, mixed | 7,806.54–7,891.16 | 1,893.36–1,907.54 | 1,840.22–1,934.45 |
| Four files, no matches | 3,916.17–4,009.56 | 920.09–933.78 | 484.98–488.27 |
| Dense context | 71.28–93.04 | 51.48–53.41 | 96.58–106.12 |
| Slow consumer | 999.72–1,001.14 | 1,001.11–1,006.74 | 1,001.85–1,001.89 |

Small-file and mixed-file rankings change with build order. Neither establishes
a parallel win. Dense context is about 1.8–2 times slower than the compiled
sequential control. No-match scans are about 1.9 times faster: without queued
results, both workers can scan to completion concurrently. The constrained consumer dominates wall time; its nominal
64 one-millisecond sleeps take much longer on this scheduler. It also includes
`ForEach-Object` pipeline binding, so this is delivered pipeline throughput,
not an isolated engine measurement.

## Allocation, CPU and first result

Dense context allocates about 210.0 MB per invocation sequentially versus
243.5–243.7 MB in parallel (decimal MB, approximately 16% more). Process CPU per
invocation increases from about 53 ms to 381–384 ms. This is a material cost even
though wall time remains below a second. The parallel path creates engine result
objects before adapting them to MatchInfo and introduces batch/channel handoffs;
profiling is needed to attribute their respective costs.

Many small files allocate about 24.7 MB sequentially versus 28.7–28.9 MB in
parallel. First result was 18.0–18.7 ms sequentially versus 23.1–26.8 ms in parallel.
The mixed workload allocates about 587.0 MB versus 592.5 MB. CPU increases from
1,934–1,947 ms to 2,094–2,153 ms without a consistent wall-time improvement.
Slow-consumer CPU samples vary substantially between orders; they do not support
a stable CPU ranking.

## Interpretation and next experiment

Whole-file ordering limits useful overlap when matches are frequent. Later files
fill their bounded queues and pause while the first file is drained. The barriers
in the engine tests verify this behavior; the performance explanation remains an
inference until profiling attributes the time. Increasing buffers indiscriminately
would trade memory for speculative work and does not remove the ordering constraint.

Next, investigate batch handoff and result-adaptation cost on dense output while
retaining the existing boundedness and cancellation tests. Compare a small number
of batch sizes against the compiled sequential control. Defer a broad 1/2/4-worker
sweep until that regression is addressed. No cold-storage or memory-mapping claim
follows from these warm-cache runs.

## Reproduction and artifacts

Use `Measure-SelectString.ps1` with the three executable paths and labels above,
`-Scenario PatternSmallFiles -PatternFamily SqlDuration -WarmupIterations 3
-Iterations 5 -LatencyIterations 1 -Affinity 0 -TimeoutSeconds 300`, and the
record count, lines per file, input kind and output mode from the workload table.
For the slow consumer, add `-SinkDelayMilliseconds 1 -SinkDelayEvery 128`.
Repeat with the executable/label arrays reversed and separate artifact directories.

Raw results, including all samples, assembly hashes, CPU, GC collections and
process-wide peak working set, are local in `tmp/SelectString/parallel-{small,
large-mixed,large-no-match,dense-context,slow-consumer}-{forward,reverse}`. Peak working set
includes fixture construction and validation and is not a queue-memory measure.
The local runner is `tmp/SelectString/Measure-Parallel.ps1`. The harness now supports
multi-file context validation and the optional blocking sink; neither changes
the production scanner.
