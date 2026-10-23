# Regex promotion across many small files

Measured 2026-10-23 with the same isolated binary as
[the large-log comparison](RESULTS-background-log.md). Production sources and
compilation policy are unchanged.

## Method

Each invocation searches 4,096 corpus records in either one UTF-8 file or 256 files
of 16 lines each. All file paths are supplied to one Select-String invocation in
explicit order. The matcher is shared across files. Fixture generation and path
array construction are outside timing. Files are warm-cache; this does not
measure cold-storage performance.

Two inputs use the existing independently validated corpus:

- Literal: all records match `alpha`, producing 4,096 results.
- SQL duration: cycles through a captured match, absent input, short near miss
  and a 4 KiB near miss, producing 1,024 results with a duration capture.

Validation checks file path, local line number, input text and captures before
timing. Separate harness tests use 17 records split into six files with three
lines per file (two in the last), plus an absent-input case, to exercise boundaries
that do not align with the four-example corpus cycle.

Shipping/current controls and six modes of the experimental binary run in forward
and reverse order. Each case uses a fresh worker, 30 warmups, 15 timed invocations,
five separate first-result passes and affinity 5 (two verified physical cores).
Output uses null redirection. CPU includes timing-loop bookkeeping; total wall
time includes joined disposal. Ranges are paired run values, not confidence
intervals. Short CPU windows are coarse. No recording is started by the runner.

## Results

All 64 cases passed count/capture/order validation, contributing 960 timed
invocations. Binary hashes were stable across orders. All 256-file promotion cases
completed exactly one adapter per invocation, rather than one per file.

### Literal

| Mode | One file ms | 256 files ms | 256 files CPU ms | 256 files first result ms |
| --- | ---: | ---: | ---: | ---: |
| shipping | 1.71-1.71 | 7.86-9.12 | 7.29-9.38 | 1.50-1.73 |
| current | 1.04-1.06 | 8.16-8.31 | 8.33-8.33 | 1.43-1.45 |
| interpreted | 1.06-1.07 | 8.04-9.89 | 9.38-10.42 | 1.48-2.63 |
| compiled | 1.29-1.40 | 9.17-9.37 | 9.38-10.42 | 2.44-2.48 |
| background | 1.33-1.46 | 9.22-9.92 | 10.42-11.46 | 2.04-2.38 |
| background-warm | 1.02-1.06 | 7.93-9.13 | 7.29-9.38 | 1.49-1.78 |
| delayed-synchronous | 1.33-1.39 | 8.78-8.88 | 9.38-10.42 | 1.46-2.32 |
| delayed-background-warm | 1.03-1.08 | 8.15-9.26 | 6.25-9.38 | 1.46-1.69 |

### SqlDuration

| Mode | One file ms | 256 files ms | 256 files CPU ms | 256 files first result ms |
| --- | ---: | ---: | ---: | ---: |
| shipping | 118.87-119.07 | 122.62-127.14 | 135.42-137.50 | 0.59-0.60 |
| current | 117.41-117.63 | 124.04-131.34 | 128.12-139.58 | 0.59-0.60 |
| interpreted | 117.10-122.18 | 126.23-129.45 | 133.33-134.38 | 0.60-0.65 |
| compiled | 32.07-32.22 | 41.64-41.67 | 43.75-45.83 | 5.28-5.82 |
| background | 33.24-33.30 | 42.88-43.34 | 46.88-48.96 | 2.90-5.07 |
| background-warm | 32.59-33.38 | 40.00-40.22 | 42.71-44.79 | 3.13-3.59 |
| delayed-synchronous | 45.27-45.52 | 54.24-55.29 | 58.33-61.46 | 2.78-2.92 |
| delayed-background-warm | 40.51-46.29 | 56.21-56.74 | 61.46-64.58 | 2.86-3.10 |


## Interpretation

The mixed SQL workload benefits across file boundaries: immediate compilation or
background promotion reduces 256-file wall time from 126-129 ms to 40-43 ms,
about 66-69%. Process CPU falls from 133-134 ms to 43-49 ms. The exploratory
128-record delayed modes take 54-57 ms. Per-file size alone would miss the
amortization opportunity: these small files share one invocation's regex.

The literal case shows no reliable compilation benefit. The one-file interpreted
control takes about 1.06 ms; splitting into 256 files increases it to 8.04-9.89 ms.
Background warm modes overlap that range. Cheap-pattern results vary by run and
CPU windows are short; do not rank those small differences. These data support
considering matching work as well as the number of files/records.

For SQL across 256 files, instrumented first-result medians are 0.60-0.65 ms
interpreted, 5.28-5.82 ms compiled and 2.78-5.07 ms across delayed/background modes.
The throughput win therefore does not establish a first-result latency win.
The separate latency passes include downstream counting. No ETW evidence here
isolates why their first-result costs differ from the one-file case.

All 270 timed background invocations outside the single-file literal case started
and promoted. For the 90 single-file literal calls, 86 started compilation and
81 promoted: nine admitted tasks were never used, including four that did not
start compilation. All cases had zero faults/rejections/outstanding workers.
Total drain across those 90 literal calls was about 1.35 ms. Joined cleanup keeps
that cost accounted for; these results do not choose a production cleanup policy.

Next cover context and output modes (Quiet/List/Raw), then multiple patterns.
Production admission remains undecided; neither unconditional compilation nor the
128-record control is justified by the combined literal/expensive-pattern results.
## Reproduction

See the `PatternSmallFiles` example in [README.md](README.md). Use
`PatternFile,PatternSmallFiles`, `RecordCount 4096`, `LinesPerFile 16`,
`CaseSensitive`, culture `en-US`, and the measurement settings above. Literal uses
`InputKind Match`; SqlDuration uses `InputKind Mixed`. Retain all six promotion
modes plus shipping/current controls.

Local artifacts (unversioned):

- `tmp/SelectString/Run-PromotionSmall.ps1` and `Summarize-PromotionSmall.ps1`
- `tmp/SelectString/promotion-small2-forward/` and `promotion-small2-reverse/`
- `tmp/SelectString/promotion-small-summary.json` and `promotion-small-source.json`
- `tmp/SelectString/small-files-harness-final.log`

The interrupted `promotion-small-forward/` pilot had an uninitialized path array
and is excluded. The final harness passes before both complete run orders.
Assembly/source hashes accompany the raw data. Experimental utility SHA256:
`470488E076C5F17681F37308B5A06A1DF0C754461653DBA042BF05F7874687C0`.
