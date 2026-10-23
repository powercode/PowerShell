# Common regex pattern families

Measured 2026-10-23. The first batch covers nine search-oriented families rather
than extrapolating from the capturing log regex. Literal, `alpha.*omega` and
`alpha.+omega` are authored controls. Catalog pattern URLs accompany fixtures in
PatternFamilies.cs; anchors and expressions are preserved. These are benchmark
workloads, not recommendations for domain validation.

## Method and validation

BenchmarkDotNet 0.16.0-preview.2, .NET 11.0.0-rc.2.26504.105, Windows 11,
Core i9-13900K, affinity 1. Short jobs: one launch, three warmup and three measured
iterations. All 36 initial cases completed. Means below are exploratory; the raw
reports retain confidence intervals, which are wide for some short operations.

Each operation processes 10,000 prepared strings through the actual engine.
ConstructAndMatch creates an engine once per operation, including compilation and
first-use work; ReuseAndMatch reuses a warmed engine. No file I/O, pipeline binding,
context or output-sink costs are included. Case-sensitive, first-match mode was
measured. Both modes still construct the engine's match metadata.

Mixed input cycles equally through success, an absent prefix, a short near miss
and a roughly 4 KiB near miss. There are 2,500 matching records. Long near misses
are deliberately different by family: wildcard patterns lack the required suffix,
SQL duration has a long digit run followed by the wrong unit, assignment lacks a
closing quote, and bounded patterns exceed limits or fail their trailing anchor.
This is a failure-path exercise, not an observed production distribution. Compare
interpreted/compiled within each family; do not rank regex shapes from these
different inputs as if all did equivalent work.

All 46 standalone tests pass. The fixture tests exercise nine families in eight
combinations of compilation, case sensitivity and AllMatches. Authored expectations
check success/failure, match position/value, group count and group values. Setup
also validates the selected variant before timing. These tests are a starting
corpus, not exhaustive equivalence proof or a pattern classifier.

## Mixed-input results

Mean milliseconds per 10,000 records:

| Family | Fresh interpreted | Fresh compiled | Reused interpreted | Reused compiled |
| --- | ---: | ---: | ---: | ---: |
| Literal | 0.619 | 0.998 | 0.609 | 0.596 |
| DotStar | 49.422 | 2.296 | 48.715 | 1.250 |
| DotPlus | 48.901 | 2.271 | 49.281 | 1.219 |
| SQL duration | 270.560 | 69.026 | 272.889 | 66.628 |
| Order ID | 0.843 | 1.643 | 0.829 | 0.678 |
| Image extension | 0.827 | 2.107 | 0.834 | 0.270 |
| PR reference | 0.670 | 1.737 | 0.680 | 0.478 |
| Markdown heading | 0.532 | 1.857 | 0.545 | 0.292 |
| Secret assignment | 10.888 | 2.831 | 10.848 | 1.169 |

Literal reuse shows little difference within the uncertainty of this run, while
fresh compilation adds about 0.38 ms. Several structural patterns match faster
when reused but do not recover compilation cost within this invocation. Conversely,
DotStar/DotPlus, SQL duration and assignment recover their setup cost readily on
these failure-heavy inputs. Absence of captures clearly does not imply negligible
compilation benefit: the two wildcard controls have no captures.

Reused managed allocations are identical between variants within each family:
about 586 KiB for literal/wildcard/order ID, 684 KiB for single-capture patterns,
and 801 KiB for the two-capture heading. Fresh compilation adds roughly 6–21 KiB
over fresh interpretation, depending on family. Generated native code is excluded
from MemoryDiagnoser allocations.

Raw evidence: tmp/SelectString/pattern-families, sibling pattern-families.log and
source.json with source hashes. The full parameter space also supports separate
input kinds, one-record calls and case-insensitive matching; it was not all run.
Other reviewed catalog families remain later coverage, not measured evidence.

## Separating the simple controls

A further 18 cases measured fresh construction plus 10,000 matches, absent-prefix
inputs or long near misses, with the same settings. All completed successfully.

| Family | Input | Interpreted ms | Compiled ms |
| --- | --- | ---: | ---: |
| Literal | Match | 0.460 | 0.830 |
| Literal | Absent | 0.204 | 0.465 |
| Literal | Long near miss | 1.506 | 1.770 |
| DotStar | Match | 0.792 | 1.745 |
| DotStar | Absent | 0.193 | 0.481 |
| DotStar | Long near miss | 198.199 | 4.885 |
| DotPlus | Match | 0.801 | 1.732 |
| DotPlus | Absent | 0.204 | 0.481 |
| DotPlus | Long near miss | 193.590 | 4.810 |

The wildcard gains in the mixed workload are driven by expensive long failures.
Short successes and absent-prefix inputs do not recover compilation cost in these
10,000-record invocations. Literal compilation adds about 0.26–0.37 ms across these
cases, with no established matching-throughput benefit from the reuse comparison.
This supports considering literals separately, but the measured controls are ASCII,
case-sensitive patterns; case-insensitive/cultural and escaped forms remain to test.

Raw follow-up: tmp/SelectString/pattern-controls and pattern-controls.log. These
remain short exploratory runs, not portable speedups or production thresholds.

## Implications for background compilation

These data support investigating pattern-aware scheduling, but do not define a
safe recognizer or justify a universal policy. Starting a background compilation
for literal-only work could consume CPU without useful matching savings. Wildcard
or capture-heavy failure paths may offer more opportunity. The focused controls
show that even wildcard compilation can lose on short successes and cheap rejects.
Next confirm representative patterns in real pipelines/files and extend case and
culture coverage before using the observations as a production classification rule.

The proposed background variant remains planned: begin interpreted, prepare a
compiled replacement off-thread, and publish it between calls. Compare total CPU
and allocations as well as foreground latency/throughput; include abandoned work
after short pipelines, bound concurrency, preserve construction-time culture and
verify where first-use JIT occurs. Measure both affinity-1 contention and available
spare cores. No production matching or scheduling changes were made here.

## End-to-end pattern confirmation

The file and pipeline harness now consumes the same authored pattern corpus as
the engine benchmarks. `PatternFile` uses a UTF-8 file; `PatternRecords` sends the
same 10,000 lines through one cmdlet invocation. Each invocation constructs its
regex once. Successful matches, absent prefixes and long near misses are separate
cases for Literal, DotStar, DotPlus and SQL duration.

Measurements use shipping, the retained current build and the isolated
always-compiled build, in forward and reverse executable order. Each worker has
30 warmups, 15 timed calls and five separate instrumented latency calls; affinity
1, en-US, case-sensitive, warm cache, output redirected to null. Fixture creation,
validation and Add-Type are outside timing. Line numbers, order, spans and captures
are validated against authored expectations before timing. All benchmark binaries
retain their previous identities; the current/compiled reader policy is identical.

Raw results and the run/summarization scripts are retained under
`tmp/SelectString/pattern-endtoend-*`, `Run-PatternComparison.ps1` and
`Summarize-PatternComparison.ps1`. Source hashes are in
`pattern-endtoend-source.json`. The aggregate preserves individual worker samples
and assembly hashes. These fixtures isolate input shapes; they do not establish
how often expensive near misses occur in production.

File scans allocate decoded lines; pipeline inputs are prepared strings reused
across calls. Compare builds within each scenario, not file and pipeline allocation
totals as equivalent work. No-match cases have no first-result latency; successful
match cases remain necessary for evaluating startup responsiveness.

The table below reports the range of the two worker medians in milliseconds,
not a confidence interval. Comparisons of small differences remain exploratory;
the run order is reversed, but this is not a randomized repeated experiment.
The standalone suite now has 50 passing tests, including interpreted/compiled
Turkish-I behavior and retention of construction-time culture after the current
culture changes. Harness checks cover forwarding of case/culture parameters,
ordered capture validation, mixed inputs and zero-result latency semantics.
| Pattern | Input | Scenario | Shipping ms | Current ms | Compiled ms |
| --- | --- | --- | ---: | ---: | ---: |
| Literal | Match | PatternFile | 3.99–4.30 | 2.19–2.33 | 2.37–2.46 |
| Literal | Match | PatternRecords | 31.70–31.82 | 29.22–29.30 | 30.66–31.47 |
| Literal | Absent | PatternFile | 1.00–1.03 | 0.74–0.78 | 1.08–1.20 |
| Literal | Absent | PatternRecords | 27.11–28.87 | 26.31–26.93 | 27.01–27.38 |
| Literal | LongNearMiss | PatternFile | 26.67–27.29 | 17.35–17.66 | 18.37–19.66 |
| Literal | LongNearMiss | PatternRecords | 28.08–29.44 | 29.63–30.35 | 29.57–29.83 |
| DotStar | Match | PatternFile | 4.53–4.77 | 2.86–2.92 | 3.15–3.39 |
| DotStar | Match | PatternRecords | 31.34–34.78 | 31.35–31.49 | 31.42–32.52 |
| DotStar | Absent | PatternFile | 1.02–1.03 | 0.73–0.79 | 1.05–1.08 |
| DotStar | Absent | PatternRecords | 25.89–27.86 | 26.50–26.73 | 28.20–28.50 |
| DotStar | LongNearMiss | PatternFile | 223.87–226.68 | 211.81–219.00 | 23.55–25.68 |
| DotStar | LongNearMiss | PatternRecords | 206.61–207.95 | 205.92–207.79 | 32.99–33.61 |
| DotPlus | Match | PatternFile | 4.59–4.77 | 2.94–3.25 | 3.28–3.46 |
| DotPlus | Match | PatternRecords | 30.17–30.80 | 31.36–32.85 | 30.54–30.67 |
| DotPlus | Absent | PatternFile | 0.97–1.09 | 0.73–0.76 | 1.07–1.09 |
| DotPlus | Absent | PatternRecords | 28.05–28.24 | 26.78–27.23 | 26.98–27.81 |
| DotPlus | LongNearMiss | PatternFile | 221.75–223.41 | 213.76–215.74 | 22.53–23.18 |
| DotPlus | LongNearMiss | PatternRecords | 207.64–210.81 | 206.20–209.83 | 31.99–33.60 |
| SqlDuration | Match | PatternFile | 4.94–5.06 | 3.12–3.35 | 4.55–4.71 |
| SqlDuration | Match | PatternRecords | 30.50–30.85 | 30.02–31.85 | 31.98–32.28 |
| SqlDuration | Absent | PatternFile | 1.11–1.12 | 0.77–0.80 | 1.10–1.14 |
| SqlDuration | Absent | PatternRecords | 28.12–28.58 | 25.84–27.15 | 27.53–27.59 |
| SqlDuration | LongNearMiss | PatternFile | 1160.61–1237.71 | 1160.04–1167.46 | 291.68–293.10 |
| SqlDuration | LongNearMiss | PatternRecords | 1145.51–1219.81 | 1146.83–1150.61 | 274.02–278.63 |


Both orders confirm the same useful distinction. For these inputs, compilation
reduces long wildcard near-miss time by roughly 88–90% in files and 84% in
pipelines. SQL long near misses improve about 75%. Literal file scans do not
benefit, and successful/absent short inputs generally do not recover compilation
setup. Some pipeline differences are small or change with order; they do not
support a classifier by themselves.

Across the successful-match cases, instrumented first-result medians range from
0.18–0.42 ms for current to 0.71–2.54 ms for compiled. These include counting-sink
instrumentation, not just regex construction. All 144 cases were measured, counts
agree with validation, terminal highlighting is disabled, and each revision has
one consistent assembly hash.

The next experiment is interpreted startup with bounded background compilation,
using both cheap and expensive inputs. It must measure worker completion and total
CPU as well as foreground throughput/latency. No production compilation policy is
selected by this checkpoint.
