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
