# Background regex compilation prototype

Measured 2026-10-23. This is an opt-in standalone experiment, not a change to
Select-String's production engine or a proposed scheduling policy.

## Method

`BackgroundCompilation.cs` wraps the real engine. An invocation starts interpreted,
admits at most one speculative task through a shared single-worker slot, and swaps
the whole engine at a matching-call boundary. Busy admission falls back without
queuing. Construction culture is captured before scheduling. Completion suppresses
work that has not begun and drains admitted work; faults are observed. Returning
Match objects remain valid after replacement. Six new tests bring the engine suite
to 56 passing tests. The benchmark project builds without warnings/errors.

The custom probe is not a BenchmarkDotNet job. It measures four pattern families,
three input shapes, one or 10,000 records and six modes. Each case rotates mode
order through three warmups and nine measured calls. Two runs per affinity setting
(one logical CPU versus unrestricted OS affinity) produce 576 case results and
5,184 measured calls. The second pair reverses affinity order. The runtime reports
32 processors in both settings; process affinity, not ThreadPool sizing, constrains
the one-CPU run. Unrestricted execution does not fix physical-core placement.

Modes are interpreted, compiled, synchronous compilation after 128 records,
background constructor-only, background constructor plus first execution, and the
latter delayed until 128 records. The gate is an experimental control, not a selected
threshold. Worker first execution uses the selected authored fixture input. No
arbitrary user patterns or warmup inputs are executed by production code.

All calls validate match counts. Corpus validation outside timing checks spans and
captures for interpreted and compiled engines; tests separately cover replacement,
culture, retained captures, faults, admission limits, draining and the delayed gate.

Raw JSON: `tmp/SelectString/promotion-one-core`, `promotion-unrestricted` and their
`-repeat` directories. `promotion-summary.json` retains all samples;
`promotion-source.json` records source and benchmark assembly hashes. The summarizer
is `tmp/SelectString/Summarize-Promotion.ps1`. Run instructions are in README.md.

## Total wall time

Ranges of the two run medians, in milliseconds, including background drain.
These are exploratory observations, not confidence intervals. Foreground completion,
first-result time, total time, CPU, allocations and promotion observations remain
available per call in JSON. The engine-only loop omits pipeline, file I/O and output
costs; it does not measure concurrent cmdlet invocations or downstream exceptions.
| Case | Mode | One CPU total ms | Unrestricted total ms |
| --- | --- | ---: | ---: |
| Literal/Match/1 | Interpreted | 0.001-0.001 | 0.004-0.004 |
| Literal/Match/1 | Compiled | 0.334-0.338 | 0.332-0.343 |
| Literal/Match/1 | DelayedSynchronous | 0.006-0.006 | 0.006-0.006 |
| Literal/Match/1 | BackgroundConstructor | 0.055-0.057 | 0.013-0.018 |
| Literal/Match/1 | BackgroundWarm | 0.044-0.045 | 0.010-0.015 |
| Literal/Match/1 | BackgroundDelayedWarm | 0.001-0.003 | 0.004-0.005 |
| Literal/Match/10000 | Interpreted | 0.840-0.863 | 0.824-0.838 |
| Literal/Match/10000 | Compiled | 1.043-1.047 | 0.991-0.993 |
| Literal/Match/10000 | DelayedSynchronous | 1.038-1.130 | 0.986-1.027 |
| Literal/Match/10000 | BackgroundConstructor | 0.935-1.159 | 1.000-1.038 |
| Literal/Match/10000 | BackgroundWarm | 0.904-0.928 | 0.742-0.757 |
| Literal/Match/10000 | BackgroundDelayedWarm | 0.914-0.923 | 0.762-0.814 |
| DotStar/LongNearMiss/10000 | Interpreted | 197.870-209.300 | 187.282-196.676 |
| DotStar/LongNearMiss/10000 | Compiled | 5.157-5.159 | 4.852-4.945 |
| DotStar/LongNearMiss/10000 | DelayedSynchronous | 7.687-7.836 | 7.188-7.319 |
| DotStar/LongNearMiss/10000 | BackgroundConstructor | 13.584-14.692 | 4.789-4.869 |
| DotStar/LongNearMiss/10000 | BackgroundWarm | 17.409-33.240 | 4.816-4.825 |
| DotStar/LongNearMiss/10000 | BackgroundDelayedWarm | 19.873-35.095 | 7.294-7.425 |
| SqlDuration/LongNearMiss/10000 | Interpreted | 1178.348-1279.563 | 1131.229-1147.108 |
| SqlDuration/LongNearMiss/10000 | Compiled | 271.144-275.723 | 265.374-269.537 |
| SqlDuration/LongNearMiss/10000 | DelayedSynchronous | 283.671-286.668 | 274.421-288.986 |
| SqlDuration/LongNearMiss/10000 | BackgroundConstructor | 277.971-286.112 | 266.792-267.310 |
| SqlDuration/LongNearMiss/10000 | BackgroundWarm | 281.545-293.446 | 265.360-266.673 |
| SqlDuration/LongNearMiss/10000 | BackgroundDelayedWarm | 290.474-301.506 | 276.651-280.689 |

## Interpretation

- On unrestricted affinity, immediate background modes approach always-compiled
  throughput on expensive wildcard/SQL near misses. On one CPU, the wildcard
  background modes take about 14-35 ms versus 5.2 ms always-compiled and 198-209 ms
  interpreted. Scheduling and pre-promotion work matter, even though the gain over
  interpretation remains large. Worker first execution is not uniformly better
  than constructor-only preparation in this probe.
- The 128-record synchronous control reaches about 7.7-7.8 ms for one-CPU wildcard
  near misses, faster than these background modes. Background scheduling is not
  automatically the best way to avoid compiling short invocations.
- Immediate background work adds admission/drain overhead even when compilation
  never starts: the one-string literal one-CPU total is about 0.044-0.057 ms, versus
  about 0.001 ms interpreted and 0.33 ms compiled. Absolute overhead is small, but
  thousands of separate invocations can pay it repeatedly.
- Some unrestricted single-string calls start compilation that is never used.
  For the literal control, constructor-only has 1-2 such calls out of nine per run;
  the warm variant has 0-2. The delayed variant avoids admission entirely for the
  single-record workload. Nine samples are insufficient to estimate tail risk.
- Cheap 10k literal matching has modest and scheduling-dependent differences:
  the unrestricted warm variant is slightly faster here, while the one-CPU variant
  is slower. This does not justify always compiling literals or a pattern classifier.
- Background wildcard near-miss calls allocate about 15.3 KiB managed versus 11.6
  KiB compiled and 2.5 KiB interpreted. Native generated code is not included.
  Process CPU covers all threads through drain, but its clock is coarse: individual
  short calls can report zero, and even nine-call means vary noticeably. Do not
  rank small CPU differences from this probe. Long one-CPU wildcard CPU means are
  roughly 16-35 ms for background modes versus 186-189 ms interpreted.

## Next

The idea merits an end-to-end experiment, not a production change yet. Integrate an
explicit experimental variant into an isolated cmdlet build, preserve the current
and always-compiled controls, and measure actual files/pipelines and downstream
termination. Add overlapping invocation/admission tests and longer CPU measurement
windows. Keep lifecycle costs visible, verify constructor-only versus first-use
work under tracing if needed, and test a controlled physical-core pair before
claiming spare-core scheduling benefits. Any admission gate remains experimental.
