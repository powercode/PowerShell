# Lazy single-match arrays

The default regex path now carries one `Match` through the engine and factories.
`MatchInfo.Matches` materializes and caches the public array on access. Formatting,
parallel adaptation and batch size estimation do not force it. Single-hit emphasis
reuses the regex Match or stores a boxed literal range instead of two lists;
`AllMatches` retains eager arrays. Matches use one object field containing either
the single Match or its materialized array. Emphasis uses a separate object field
so replacing Matches cannot change highlighting.

Validation: 87 engine tests, 64 Select-String Pester tests and 152 shipping
comparisons passed. Coverage includes batch accounting, array identity, replacement
and null assignment, context cloning, emphasis and serialization. A local probe
also checked array identity with 64 concurrent readers on each of 100 results.

## Allocation checks

Local Windows Release build, .NET 11 preview, 2026-10-24. These are focused checks,
not evidence of a sustained end-to-end throughput improvement.

The pipeline probe compares the saved compiled `parallel-cmdlet` control with
`compact-matches`, using 100,000 matching input strings, one warmup and five samples
per mode. Approximate allocations below use the settled samples; MB is decimal.

| Consumer | Compiled control | Current implementation | Saving per result |
| --- | ---: | ---: | ---: |
| Discard output; Matches unread | 164.54 MB | 160.54 MB | 40 bytes |
| Read Matches for every result | 302.45 MB | 301.65 MB | 8 bytes |

The second mode includes a downstream `ForEach-Object` consumer's allocation.
Compare implementations within each mode, not the two modes against each other.
Both modes now allocate less than the compiled control: storing a Match or Match[]
in one field avoids retaining a separate single-match field after materialization.

Pipeline timings varied during warmup/tiering; do not infer a throughput win from
these samples. The pipeline probe did not explicitly force VT emphasis, so it does
not establish the savings from the new single-hit emphasis representation.
Repeat representative large-file/dense-context comparisons after direct final-result
construction removes the parallel path's intermediate objects.

## Emphasis and remaining measurements

Regex emphasis reuses its Match without allocating. Literal emphasis boxes two
integers, and the public/multiple-hit list representation boxes a pair of list
references. Those cases trade extra small objects for a smaller MatchInfo; the
pipeline probe did not force emphasis and does not measure those tradeoffs.
Tests explicitly exercise both compact emphasis representations and live public
constructor lists independently of terminal support.

An earlier matcher-only check, before storage consolidation, confirmed that
deferring the single-match array saved 32 bytes per successful match: allocation
fell from 24,000,000 to 20,800,000 bytes over 200,000 calls, half matching.
AllMatches allocation was unchanged. This isolates the array saving; the pipeline
table above measures the final representation, including result object sizes.

Local artifacts under `tmp/SelectString/`:

- Probe: `Measure-LazyMatches.ps1`.
- Compiled control results: `lazy-before.jsonl`.
- Current build and results: `compact-matches/`, `compact-after.jsonl`.
- Earlier matcher-only probe: `trymatch-check/`.
