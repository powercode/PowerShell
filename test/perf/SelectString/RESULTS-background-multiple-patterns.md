# Background regex promotion with multiple patterns

Measured 2026-10-23 with the existing isolated experiment. Production sources and
compilation policy are unchanged.

## Method

Each invocation scans one UTF-8 file with 4,096 corpus records. Literal uses only
matching inputs (4,096 results); SqlDuration cycles through a captured match,
absent input, short near miss and 4 KiB near miss (1,024 results). Three sets retain
identical expected output:

- Single: the target pattern alone.
- TargetFirst: target, OrderId, PrReference, Assignment.
- TargetLast: OrderId, PrReference, Assignment, target.

The three added corpus patterns do not match these inputs. Their source patterns
are recorded in each worker's `SearchPatterns` array. Validation checks the
winning `MatchInfo.Pattern`, line order and independently specified spans/captures.
The harness also checks both pattern orders with file and pipeline input.
These cases do not cover overlapping successful patterns or AllMatches.

Shipping/current and six modes of one experimental binary run in forward/reverse
order. Each case uses a fresh worker, 20 warmups, 15 timed invocations, five
separate instrumented latency calls, and affinity 5 (two verified physical cores).
Files are warm-cache; output uses null redirection. Timing includes joined
disposal. CPU includes timing-loop bookkeeping; short-call windows are coarse.
Ranges are paired run values, not confidence intervals. No ETW is started.

The warm background variants probe the first authored input once. Matching stops
at the first successful pattern, so TargetFirst does not exercise later patterns
during that probe; TargetLast does. A completed warm probe is not proof that all
patterns have executed. The experimental 128-record delay remains a control.

## Results

All 96 cases passed validation (1,440 timed invocations). Binary hashes remained
stable across orders. Worker faults, rejections and outstanding-worker counts were
zero. Harness checks passed before measurement.

### Literal-Single

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 1.780-1.817 | 2.083-3.125 | 0.205-0.241 |
| current | 1.067-1.081 | 2.083-3.125 | 0.212-0.245 |
| interpreted | 1.060-1.111 | 1.042-1.042 | 0.216-0.232 |
| compiled | 1.319-1.395 | 2.083-2.083 | 0.623-0.701 |
| background | 1.338-1.463 | 2.083-2.083 | 0.697-0.722 |
| background-warm | 1.138-1.145 | 2.083-2.083 | 0.292-0.303 |
| delayed-synchronous | 1.350-1.423 | 2.083-2.083 | 0.225-0.284 |
| delayed-background-warm | 1.050-1.557 | 2.083-3.125 | 0.226-0.245 |

### Literal-TargetFirst

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 1.745-2.159 | 2.083-4.167 | 0.213-0.266 |
| current | 1.065-1.103 | 2.083-2.083 | 0.223-0.254 |
| interpreted | 1.073-1.173 | 2.083-3.125 | 0.228-0.245 |
| compiled | 1.431-1.474 | 2.083-3.125 | 0.809-0.848 |
| background | 1.378-1.476 | 2.083-3.125 | 0.260-0.265 |
| background-warm | 1.127-1.134 | 3.125-3.125 | 0.302-0.306 |
| delayed-synchronous | 1.427-1.487 | 2.083-2.083 | 0.249-0.253 |
| delayed-background-warm | 1.068-1.161 | 2.083-2.083 | 0.243-0.247 |

### Literal-TargetLast

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 2.248-2.299 | 2.083-4.167 | 0.255-0.287 |
| current | 1.495-1.557 | 3.125-3.125 | 0.267-0.283 |
| interpreted | 1.475-1.566 | 2.083-2.083 | 0.247-0.302 |
| compiled | 2.902-2.957 | 3.125-3.125 | 2.011-2.134 |
| background | 2.943-3.056 | 3.125-5.208 | 0.246-0.302 |
| background-warm | 1.877-1.879 | 5.208-6.250 | 0.276-0.305 |
| delayed-synchronous | 2.999-3.013 | 4.167-5.208 | 0.264-0.282 |
| delayed-background-warm | 1.943-2.234 | 4.167-5.208 | 0.249-0.296 |

### SqlDuration-Single

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 119.300-128.284 | 144.792-147.917 | 0.230-0.241 |
| current | 118.280-119.744 | 136.458-142.708 | 0.225-0.244 |
| interpreted | 116.691-120.489 | 136.458-140.625 | 0.238-0.269 |
| compiled | 31.849-31.992 | 34.375-35.417 | 2.373-2.422 |
| background | 32.360-32.688 | 38.542-40.625 | 0.359-0.446 |
| background-warm | 32.723-32.747 | 36.458-39.583 | 0.379-0.400 |
| delayed-synchronous | 45.397-46.097 | 45.833-48.958 | 0.358-0.394 |
| delayed-background-warm | 45.527-46.255 | 50.000-54.167 | 0.375-0.411 |

### SqlDuration-TargetFirst

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 120.023-122.135 | 137.500-138.542 | 0.238-0.248 |
| current | 120.228-122.696 | 136.458-142.708 | 0.253-0.267 |
| interpreted | 118.663-119.361 | 139.583-139.583 | 0.252-0.255 |
| compiled | 33.634-35.535 | 38.542-39.583 | 2.584-2.861 |
| background | 34.871-35.520 | 40.625-43.750 | 0.462-0.664 |
| background-warm | 34.837-35.121 | 40.625-42.708 | 0.455-0.458 |
| delayed-synchronous | 48.033-48.160 | 54.167-54.167 | 0.405-0.437 |
| delayed-background-warm | 49.204-49.775 | 58.333-59.375 | 0.430-0.451 |

### SqlDuration-TargetLast

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 121.819-127.349 | 133.333-138.542 | 0.235-0.271 |
| current | 119.728-122.237 | 133.333-139.583 | 0.247-0.247 |
| interpreted | 120.822-121.769 | 136.458-139.583 | 0.275-0.277 |
| compiled | 33.810-34.255 | 37.500-37.500 | 3.708-3.758 |
| background | 35.415-35.740 | 41.667-42.708 | 0.395-0.445 |
| background-warm | 34.619-34.903 | 42.708-44.792 | 0.395-0.457 |
| delayed-synchronous | 47.550-48.160 | 51.042-52.083 | 0.418-0.442 |
| delayed-background-warm | 40.084-48.161 | 54.167-55.208 | 0.399-0.454 |


## Interpretation

Four-pattern SQL scans retain a large benefit: immediate compiled/background modes
take 34-36 ms versus 119-122 ms interpreted, about 70-72% less wall time. Process
CPU also falls, from roughly 136-140 ms to 38-45 ms. Delayed modes take 40-50 ms.
The extra nonmatching patterns do not remove the expensive SQL near-miss benefit.
Do not rank the small differences between immediate modes.

The literal case behaves differently. Target-first interpreted time remains about
1.1 ms: later patterns are never matched. Target-last interpreted time is
1.48-1.57 ms; compiled takes 2.90-2.96 ms, constructor-only background 2.94-3.06 ms,
and warmed background 1.88 ms. Even warmed compilation does not beat interpreting
this cheap four-pattern workload. Short CPU windows are too coarse for precise
CPU comparisons.

Across three background modes and both orders, each SQL set admits, starts and
promotes all 90 timed invocations. Literal TargetLast admits/starts all 90, but
only 37 promote; total disposal drain is 19.66 ms. Literal Single promotes 83 of
90 admissions (85 start), and TargetFirst promotes 86 (87 start). Promotion alone
also does not establish a benefit. These counters cover throughput calls, not
separate latency passes. The warm probe need not exercise every pattern.

First output has its own tradeoff: SQL TargetLast synchronous compilation reports
3.71-3.76 ms versus 0.28 ms interpreted and about 0.40-0.46 ms for background modes.
Five instrumented passes do not characterize scheduling tails. Earlier short-call
and joined-cleanup findings still apply. This does not establish that background
work is free or that warming arbitrary real input is an appropriate policy.

The broad matrix now supports a bounded admission experiment based on observed
matching work, retaining interpreted, always-compiled and delayed controls.
Pattern count/order belong in that assessment; file size, record count or output
mode alone remain insufficient. Keep production unchanged until admission overhead,
short-call behavior and cleanup are justified. Remaining coverage includes
multiple successful/overlapping patterns, AllMatches, combined output switches,
List across files and late successful matches.

## Reproduction

Use PatternFile with `PatternSet Single`, `TargetFirst` or `TargetLast`,
`PatternFamily Literal -InputKind Match` or `PatternFamily SqlDuration -InputKind
Mixed`, 4096 records, CaseSensitive and en-US culture. See [README.md](README.md)
for the multi-pattern example. Use the settings above and retain all eight modes/
controls. Complete ordered patterns and source/binary hashes accompany the data.

Unversioned local artifacts:

- `tmp/SelectString/Run-PromotionMulti.ps1` and `Summarize-PromotionMulti.ps1`
- `tmp/SelectString/promotion-multi-forward/` and `promotion-multi-reverse/`
- `tmp/SelectString/promotion-multi-summary.json` and `promotion-multi-source.json`
- `tmp/SelectString/multiple-patterns-harness.log`

Experimental utility SHA256:
`470488E076C5F17681F37308B5A06A1DF0C754461653DBA042BF05F7874687C0`.
