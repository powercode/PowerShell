# Regex promotion with context and output modes

Measured 2026-10-23 using the existing isolated promotion binary. Production
sources and compilation policy are unchanged.

## Method

Five cases search one UTF-8 file containing 4,096 SQL-duration corpus records:
Context (two lines before/after), Raw, List, Quiet with an early hit, and Quiet
with no match. The first four cycle through a capture, absent input, short near
miss and 4 KiB near miss; the first line matches. The last contains only 4 KiB
near misses. Context/Raw return 1,024 objects, List one MatchInfo, and Quiet one
boolean (false for the full scan). Quiet's false result measures scan completion,
not first-match latency.

Shipping/current controls and six modes of the same experimental binary run in
forward and reverse order. Each case uses a fresh worker, 20 warmups, 15 timed
invocations, five separate instrumented latency calls, and affinity 5 (two verified
physical cores). Files are warm-cache. Null redirection discards output; timing
includes joined disposal. Process CPU includes timing-loop bookkeeping and is
too coarsely sampled to rank sub-millisecond calls. No ETW session is started by
the runner. Ranges below are paired run values, not confidence intervals.

Validation outside timing checks mode-specific types/values, match lines/captures,
and logical context lines. Harness checks additionally use 17 mixed/absent records
to cover beginning/end context and empty List/Raw output. Display-context formatting
is not validated by this benchmark; the existing compatibility suite covers it.

## Results

All 80 cases passed validation (1,200 timed invocations), with stable assembly
hashes and zero worker faults or outstanding workers. Harness checks also pass.

### context

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 119.144-121.601 | 136.458-140.625 | 0.372-0.389 |
| current | 117.410-120.229 | 132.292-145.833 | 0.362-0.368 |
| interpreted | 117.856-118.002 | 136.458-137.500 | 0.371-0.380 |
| compiled | 32.621-33.062 | 35.417-40.625 | 2.443-2.805 |
| background | 32.404-32.933 | 35.417-38.542 | 2.307-2.534 |
| background-warm | 32.672-33.257 | 37.500-38.542 | 0.889-0.923 |
| delayed-synchronous | 39.077-47.186 | 51.042-52.083 | 0.634-0.903 |
| delayed-background-warm | 35.541-46.422 | 41.667-60.417 | 0.524-0.868 |

### raw

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 121.124-123.718 | 138.542-148.958 | 0.214-0.243 |
| current | 118.019-120.810 | 134.375-142.708 | 0.225-0.234 |
| interpreted | 118.502-125.995 | 141.667-142.708 | 0.219-0.238 |
| compiled | 31.830-31.857 | 33.333-34.375 | 2.380-2.586 |
| background | 32.749-33.025 | 36.458-40.625 | 0.401-0.425 |
| background-warm | 32.656-33.223 | 38.542-41.667 | 0.349-0.409 |
| delayed-synchronous | 45.963-46.425 | 47.917-51.042 | 0.361-0.436 |
| delayed-background-warm | 46.593-50.084 | 47.917-54.167 | 0.349-0.430 |

### list

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 0.116-0.133 | 0.000-0.000 | 0.187-0.201 |
| current | 0.098-0.127 | 1.042-2.083 | 0.210-0.214 |
| interpreted | 0.115-0.132 | 0.000-1.042 | 0.185-0.197 |
| compiled | 1.986-2.051 | 2.083-2.083 | 2.019-2.030 |
| background | 0.221-0.892 | 1.042-1.042 | 2.020-2.052 |
| background-warm | 0.244-1.942 | 0.000-2.083 | 0.210-0.214 |
| delayed-synchronous | 0.104-0.146 | 1.042-2.083 | 0.191-0.195 |
| delayed-background-warm | 0.108-0.138 | 1.042-2.083 | 0.189-0.192 |

### quiet-hit

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 0.097-0.116 | 0.000-1.042 | 0.178-0.205 |
| current | 0.115-0.121 | 0.000-1.042 | 0.179-0.186 |
| interpreted | 0.119-0.162 | 1.042-1.042 | 0.181-0.193 |
| compiled | 2.012-2.062 | 2.083-3.125 | 2.006-2.164 |
| background | 0.205-2.019 | 1.042-2.083 | 0.196-0.206 |
| background-warm | 0.268-1.456 | 2.083-3.125 | 0.204-0.301 |
| delayed-synchronous | 0.123-0.130 | 0.000-0.000 | 0.183-0.215 |
| delayed-background-warm | 0.126-0.159 | 1.042-1.042 | 0.190-0.213 |

### quiet-miss

| Mode | Wall ms | CPU ms | First result ms |
| --- | ---: | ---: | ---: |
| shipping | 475.701-487.616 | 485.417-506.250 | 476.712-486.985 |
| current | 475.191-487.930 | 480.208-500.000 | 474.528-479.866 |
| interpreted | 480.629-490.138 | 495.833-495.833 | 481.339-488.171 |
| compiled | 122.932-123.272 | 133.333-144.792 | 122.516-122.737 |
| background | 125.021-127.272 | 141.667-151.042 | 123.375-127.887 |
| background-warm | 124.319-124.512 | 146.875-150.000 | 123.600-127.575 |
| delayed-synchronous | 133.430-133.799 | 152.083-156.250 | 135.131-137.058 |
| delayed-background-warm | 134.119-135.284 | 156.250-158.333 | 134.350-134.804 |


## Interpretation

On the same experimental binary, immediate compiled/background modes reduce
Context/Raw wall time by roughly 72-75%, with lower process CPU as well. Full-scan
Quiet improves from 481-490 ms interpreted to 123-127 ms, about 74-75%.
The 128-record delayed controls still help these scans but take longer: roughly
39-50 ms for Context/Raw (some background Context runs reach 36 ms), and 133-135 ms
for Quiet without a match. This is evidence for these expensive near-miss inputs,
not for all patterns using these output modes.

Early List/Quiet show the opposite tradeoff. Interpreted medians are 0.12-0.16 ms;
synchronous compilation raises them to about 2 ms. Immediate background medians
vary from 0.21 to 2.02 ms. The delayed controls stay around 0.10-0.16 ms and admit
no worker before these first-line exits. The absolute cost remains small, but
repeated short invocations and latency-sensitive pipelines can expose it.

Across 60 early-exit invocations per immediate background mode, constructor-only
work starts 37 times and promotes 18 times; warmed work starts 26 times and never
promotes. All 60 warmed admissions are unused, including 34 cancelled before
compilation starts. Joined disposal spends 51.22 ms in total draining that mode's
early calls, about 0.85 ms per timed invocation on average. This illustrates why
quick first output does not establish quick completion. Worker faults/rejections
are zero. All 270 background invocations in Context/Raw/full-scan Quiet start and
promote; their total drain is negligible relative to matching.

Five separate instrumented latency calls do not characterize scheduling tails.
In particular, early constructor-only results differ between the latency and
throughput passes. Do not combine those samples or subtract their medians. CPU
quantization produces zero values for some short-call windows; that does not
mean those calls consume no CPU.

Output mode alone cannot choose compilation: Quiet can stop immediately or scan
an expensive file fully. Keep admission based on measured work under investigation,
and decide cleanup policy explicitly. Next measure multiple patterns before
choosing a production policy. Combined switches, List across multiple files and
late successful matches remain additional coverage beyond this focused matrix.

## Reproduction

See `PatternOutput` in [README.md](README.md). Run PatternFile with SqlDuration,
4096 records, CaseSensitive and en-US culture. Use Mixed input for Context, Raw,
List and early Quiet; LongNearMiss for full-scan Quiet. Use the measurement
settings above and preserve all six experimental modes plus shipping/current.

Local artifacts remain unversioned:

- `tmp/SelectString/Run-PromotionModes.ps1` and `Summarize-PromotionModes.ps1`
- `tmp/SelectString/promotion-modes-forward/` and `promotion-modes-reverse/`
- `tmp/SelectString/promotion-modes-summary.json` and `promotion-modes-source.json`
- `tmp/SelectString/output-modes-harness.log`

Source/binary hashes accompany the raw data. Experimental utility SHA256:
`470488E076C5F17681F37308B5A06A1DF0C754461653DBA042BF05F7874687C0`.
