# Bounded batch-size experiment

Measured 2026-10-24. Retain the current 32-result / 32 KiB target. Increasing
batch size reduced process CPU but did not establish a repeatable delivered
wall-time improvement. This experiment does not justify increasing queue memory.

## Setup

Same machine, runtime and warm-cache method as
[the file-parallel matrix](RESULTS-parallel-files.md). Compared compiled
sequential with two-worker builds using batch targets of 32/32 KiB,
128/128 KiB and 512/512 KiB. Each file still has two queued batches, the active
window is two files, and the first published batch still contains one result.
Only the `ScanFiles` count/byte defaults differ between experimental builds.
Their reflected defaults were checked in the built assemblies before measurement.

`PatternSmallFiles`, SQL-duration captures, all matching input, context 2,
262,144 records in four files of 65,536 lines. Three warmups, seven timed calls,
one separate counting/latency pass. Unrestricted affinity. Fresh processes per
build, forward and reverse build order. All eight workers validated complete
ordered output and context before measuring. No timing work overlapped builds
or test execution.

## Results

Wall times are per-run medians; allocation figures are per-invocation means,
in decimal MB. The two orders are separate runs, not confidence bounds.

| Build | Forward wall ms | Reverse wall ms | Forward CPU ms | Reverse CPU ms | Allocated MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compiled sequential | 56.50 | 88.06 | 60.27 | 125.00 | 209.96 |
| 32 results / 32 KiB | 64.52 | 106.98 | 314.73 | 357.14 | 243.43–243.53 |
| 128 results / 128 KiB | 102.53 | 59.30 | 250.00 | 225.45 | 242.21–242.23 |
| 512 results / 512 KiB | 98.59 | 98.21 | 142.86 | 136.16 | 242.05–242.12 |

The sequential control and the 32/128 variants vary substantially between
orders. These runs cannot select a reliable wall-time winner. The 512 variant
uses less CPU than 32 in both orders, consistent with fewer handoffs, but does
not consistently improve delivered throughput over 32 or the sequential control.
Scheduling, JIT and GC attribution requires a trace; this table cannot establish
which causes the variability.

Batch-size changes save less than 1% of total allocation. Approximately 32 MB
of excess allocation over sequential remains even with 512-result batches.
That makes removing intermediate engine-result/context objects a more useful
next experiment than a finer batch-size sweep. The current parallel adapter
constructs MatchInfo after receiving those intermediate objects; the sequential
generic scanner constructs its final output directly.

## Bounds and decision

The aggregate queued payload target rises from 128 KiB to 512 KiB to 2 MiB
across the two file slots. These are approximate payload targets, not managed
heap limits: reader/context state, producer work, the consumer batch, array
capacity and oversized individual results remain separate costs.

Expanded deterministic tests cover both full drain and early stop at batch
sizes 1, 128 and 512. They verify the earliest scanner cannot advance beyond
one consumed result plus two full queued batches, the later scanner stops at
its initial one-result batch plus one full batch, and a third file is not
admitted while both slots remain occupied. All 83 engine/prototype tests passed.
The 512-result build also passed asynchronous pipeline stop and exclusive file
reopen checks.

No production default changed. Preserve the smaller bound while investigating
direct final-result construction with an immutable factory snapshot. Keep
PowerShell services and output calls on the pipeline thread. Recheck delivered
throughput and allocations against compiled sequential after that change;
do not expand worker count or buffers merely to hide ordered-output stalls.

## Reproduction

Local experimental builds: `tmp/SelectString/parallel-batch128` and
`parallel-batch512`; controls: `parallel-sequential` and `parallel-cmdlet`.
Use the shared measurement wrapper with:

```powershell
-Scenario PatternSmallFiles -PatternFamily SqlDuration -InputKind Match `
    -PatternOutput Context -RecordCount 262144 -LinesPerFile 65536 `
    -WarmupIterations 3 -Iterations 7 -LatencyIterations 1 -Affinity 0
```

Run both build orders with separate artifact directories. The local runner is
`tmp/SelectString/Measure-BatchSizes.ps1`. Raw samples, assembly hashes, CPU,
GC and latency records are in `tmp/SelectString/batch-sizes-{forward,reverse}`;
build logs are `tmp/SelectString/parallel-batch{128,512}-build.log`.
