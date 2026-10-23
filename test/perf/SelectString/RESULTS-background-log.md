# Background regex promotion on large logs

Measured 2026-10-23 using the isolated cmdlet experiment described in
[RESULTS-background-cmdlet.md](RESULTS-background-cmdlet.md). Production matching
policy is unchanged.

## Method

The existing warm-cache UTF-8 log fixture contains 924,366 lines and 314,573,210
bytes (300 MiB). `LogSparse` selects every thousandth line; `LogAllMatches`
selects every line and returns a second match on every seventeenth line. Both
patterns capture level, request ID and duration. Validation checks line order,
match count and each named capture outside the timed region.

Shipping and current binaries are controls. Six modes use the same experimental
binary, retaining its reader and output paths. Each case runs in a fresh worker:
10 warmups, 15 timed invocations, and three separate instrumented latency passes.
The eight variants run in forward and reverse order. Affinity mask 5 permits two
previously verified distinct physical cores; it does not pin each thread to a
specific core. Output is discarded with null redirection. The runner did not start
an ETW recording.

Wall time includes joined disposal. CPU is the process total over the full timing
loop, including bookkeeping. Managed allocation excludes native generated code.
First-result latency comes from separate instrumented calls, so it must not be
subtracted from the throughput timings. Ranges below are the two run values,
not confidence intervals. This is synthetic, warm-cache evidence on one machine.

## Results

All 32 cases passed validation: 925 sparse results/matches, or 924,366 dense
results containing 978,741 matches. Each case contributed 15 timing samples
(480 total). Assembly and fixture hashes were stable across both orders.

### Sparse matches

| Mode | Median wall ms | Mean CPU ms | First result ms | Managed MiB/call |
| --- | ---: | ---: | ---: | ---: |
| shipping | 290.32-299.29 | 295.83-314.58 | 0.41-0.43 | 620.46-620.46 |
| current | 214.59-217.21 | 225.00-232.29 | 0.35-0.35 | 620.46-620.46 |
| interpreted | 220.47-222.43 | 227.08-234.38 | 0.35-0.37 | 620.46-620.46 |
| compiled | 208.20-216.07 | 216.67-232.29 | 2.38-2.76 | 620.47-620.47 |
| background | 208.67-214.68 | 228.12-241.67 | 2.34-2.35 | 620.49-620.49 |
| background-warm | 202.14-210.00 | 221.88-232.29 | 0.38-0.39 | 620.49-620.49 |
| delayed-synchronous | 208.93-215.90 | 220.83-235.42 | 0.35-0.42 | 620.48-620.48 |
| delayed-background-warm | 209.94-211.52 | 226.04-235.42 | 0.37-0.38 | 620.49-620.49 |

### All matches

| Mode | Median wall ms | Mean CPU ms | First result ms | Managed MiB/call |
| --- | ---: | ---: | ---: | ---: |
| shipping | 495.50-523.24 | 513.54-520.83 | 0.46-0.49 | 1342.95-1342.95 |
| current | 414.67-416.38 | 423.96-429.17 | 0.51-0.52 | 1307.73-1307.73 |
| interpreted | 408.52-416.99 | 413.54-425.00 | 0.47-0.53 | 1307.73-1307.73 |
| compiled | 322.68-326.83 | 336.46-341.67 | 2.63-2.66 | 1307.75-1307.75 |
| background | 313.66-335.26 | 330.21-343.75 | 2.52-2.58 | 1307.76-1307.76 |
| background-warm | 322.55-329.89 | 342.71-342.71 | 0.40-1.27 | 1307.76-1307.76 |
| delayed-synchronous | 328.06-347.83 | 334.38-365.62 | 0.48-0.94 | 1307.76-1307.76 |
| delayed-background-warm | 325.66-335.33 | 338.54-345.83 | 0.38-0.53 | 1307.76-1307.76 |

## Interpretation

Relative to the same-binary interpreted control, synchronous compilation reduces
AllMatches wall time by 20-23%; the background variants reduce it by about 19-23%.
CPU falls as well (roughly 414-425 ms interpreted versus 330-346 ms for background
modes). The experiment therefore shows a sustained-work benefit beyond simply
moving preparation onto another core. These results do not identify a winning
background mode.

Sparse scanning improves less: synchronous compilation saves 2-6% and background
variants about 3-8%, depending on mode/order. CPU ranges overlap. The larger gain
against shipping also includes prior engine/buffer changes and must not be
attributed to compilation alone. Managed allocation is essentially unchanged
between experimental modes; compilation is not solving the per-line allocation
cost here.

Constructor-only background compilation retains a 2.3-2.6 ms first-result cost,
close to synchronous compilation. Warm variants usually return sooner, but the
immediate warm AllMatches median ranges from 0.40 to 1.27 ms across runs. Three
latency passes per case are too few to characterize scheduling tails. Delayed
synchronous compilation also permits early output; it can stall later output.

All 180 timed background invocations were admitted, started and promoted, with
zero rejections/faults/outstanding workers. Total disposal drain was below 0.011 ms
per 15-invocation case. That says little about early termination: these scans
outlast compilation. The earlier short-call lifecycle results still apply.

The next workload should be many small files within one invocation, followed by
context/output modes and multiple patterns. Keep the matching engine shared
across files. Work-based admission and cleanup policy remain open; neither the
128-record control nor fixture-based warming is a production recommendation.
## Reproduction and raw evidence

Run `Measure-SelectString.ps1` for each mode with `-Scenario LogSparse,LogAllMatches`,
`-LogFixture ./tmp/SelectString/log-fixture`, `-WarmupIterations 10`, `-Iterations 15`,
`-LatencyIterations 3`, and `-Affinity 5`. Use the shipping/current executables as
controls without `-PromotionMode`; experimental modes and build instructions are
documented in [README.md](README.md).

Local raw data and orchestration remain outside version control:

- `tmp/SelectString/Run-PromotionLog.ps1`
- `tmp/SelectString/Summarize-PromotionLog.ps1`
- `tmp/SelectString/promotion-log-forward/` and `promotion-log-reverse/`
- `tmp/SelectString/promotion-log-summary.json` and `promotion-log-source.json`

Fixture SHA256: `7B7A77BDCD687645D8B1297513F741A3178529008BBF98243DD8E12EC5F15380`.
Experimental utility assembly SHA256:
`470488E076C5F17681F37308B5A06A1DF0C754461653DBA042BF05F7874687C0`.
