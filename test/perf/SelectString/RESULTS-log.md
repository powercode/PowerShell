# Large-log capture benchmark

The deterministic fixture contains 924,366 UTF-8 log entries in 314,573,210 bytes
(just over 300 MiB). SHA256:
`7B7A77BDCD687645D8B1297513F741A3178529008BBF98243DD8E12EC5F15380`.
It varies timestamps, severity, request IDs, durations, messages, routes and item
lists. Bucket queries select approximately 0.01%, 0.1%, 1%, 10% and 100% of lines
without regenerating the file. Every seventeenth line has a second event record.
This is synthetic, regularly spaced data; real error bursts/multiline exceptions
and cold storage remain separate workloads.

The workload validates line order, result count, regex match count and all named
capture values before timing. Expected output lines are 93 / 925 / 9,244 /
92,437 / 924,366 for the five densities. AllMatches returns 924,366 output lines
containing 978,741 regex matches; Context returns 925 results with two context
lines requested on each side. Context field semantics remain covered by the
existing engine/differential tests.

## Validation

All seven scenarios ran successfully on shipping, extraction and concrete queues
with the 300 MiB fixture. A small fixture was also used for the initial 21-case
smoke test. Final-generator determinism was checked by independently generating
two 1 MiB files with identical hashes. Negative validator checks rejected missing
results and incorrect captures. LogAllMatches/LogContext passed with markers
and SkipCounting enabled; ordinary String/DenseContext harness smoke checks
also passed. PowerShell parsing and git diff whitespace checks passed.
No engine code changed and no elevated WPR recording was started by the agent.

## Initial measurements

Windows x64, i9-13900K, .NET 11.0.0-rc.2.26504.105, Release binaries, affinity 1.
Fresh worker per case; validation then three explicit warmups and five measured
calls. Default tiering, warm filesystem cache, null redirection, no ETW. Counting
and first-result latency use a separate pass. This is an exploratory run, not a
stable speedup estimate. The large validation pass itself exercises runtime tiering.
Peak working set includes compiler/setup/validation/counting, not just scanning.

The reverse-order run (queues, extraction, shipping) is the clean comparison
below. Earlier development runs are retained locally; a short harness smoke test
ran alongside part of the forward final-fixture run, so those timings are not
used as clean comparative evidence.

| Scenario | Shipping ms | Extraction ms | Queues ms | Queues MiB/s |
| --- | ---: | ---: | ---: | ---: |
| LogAllMatches | 502.52 | 544.38 | 500.28 | 599.66 |
| LogBroad | 422.52 | 372.21 | 361.18 | 830.61 |
| LogContext | 434.36 | 457.20 | 361.14 | 830.70 |
| LogDense | 429.44 | 450.57 | 438.02 | 684.91 |
| LogMedium | 362.01 | 330.17 | 321.44 | 933.30 |
| LogRare | 312.32 | 277.17 | 277.60 | 1080.70 |
| LogSparse | 353.97 | 319.18 | 338.26 | 886.88 |

These results are less dramatic than the short-line microbenchmarks. Queue-build
AllMatches was effectively tied with shipping in the clean run; dense default
output was about 2% slower. Sparse/context/broad-query scans improved, but this
single short run cannot establish stable percentage changes. The forward run's
AllMatches slowdown did not reproduce. Do not claim a general large-file speedup.

Even rare matches allocate about 650 MB per call across builds, mostly consistent
with materializing decoded lines. This is an inference from allocation totals,
not an allocation-stack attribution. Queue-build dense output allocates about
1.219 GB vs shipping's 1.256 GB; AllMatches about 1.371 GB vs 1.408 GB; sparse
context about 650.82 MB vs 680.47 MB. MiB/s uses actual manifest bytes and median
elapsed time. Allocation totals include the harness and are decimal MB/GB.

All queries match the first line. Their first-result measurements therefore do
not model delayed first hits; the separate counting pipeline also adds overhead.
Matching is case-sensitive. Changing density also changes the regex bucket
selector; differences between density scenarios are not purely output costs.

## Next capture

The fixture already exists at `tmp/SelectString/log-fixture`. From an elevated
PowerShell at the repository root:

```powershell
./test/perf/SelectString/New-EtwCapture.ps1 `
    -Scenario LogAllMatches -LogFixture ./tmp/SelectString/log-fixture `
    -WarmupIterations 3 -Iterations 5 -TimeoutSeconds 600
```

Follow with LogSparse or LogContext to contrast output density/context costs.
Inspect the five measured regions; setup and full validation are also captured.
The next engine optimization remains deferred until this workload is profiled.

## Local artifacts

- Fixture: `tmp/SelectString/log-fixture/application.log` and `manifest.json`.
- Clean measurement: `tmp/SelectString/log-300-reverse/results.json`.
- Forward exploratory validation: `tmp/SelectString/log-300-final/results.json`.
- Final marker/SkipCounting smoke: `tmp/SelectString/log-marker-smoke/results.json`.
- `log-300-results` and `log-smoke-results` are development runs with the earlier
  filler-message generator; their fixture hashes differ and timings are not
  combined with this report.

The compared executables are the same shipping, first-extraction and
concrete-queues binaries documented in RESULTS-matrix.md. JSON contains assembly
hashes, fixture manifest, individual timing samples, allocations, GC counts,
first-result latency, working set and validated counts. The README documents
fixture generation and the full comparison command.
