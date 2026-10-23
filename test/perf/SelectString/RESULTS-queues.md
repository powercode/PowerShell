# Concrete output queues

Measured 2026-10-23. The engine now exposes its internal output queues as
`List<T>` and holds the logical/display trackers by their concrete types.
This removes boxed enumerators when draining queues and reduces interface
dispatch. The context algorithm, result ownership and cancellation checks remain
unchanged. The change also benefits scans without context.

Validation: 28 standalone tests, 56 Pester 4.10.1 tests and 138 differential
comparisons with shipping passed. Resources and types were regenerated for the
framework-dependent Release build. NuGet audit was disabled for this local build
because the audit endpoint was inaccessible; dependency versions were unchanged.

## Measurements

All runs used .NET 11.0.0-rc.2.26504.105, an i9-13900K, affinity mask 1,
100,000 input lines, 60 warmups and 25 measured iterations per fresh worker.
Output was discarded with null redirection; tracing and counting were disabled.
The second run reversed build order. Ranges below are the two run medians,
not confidence intervals. Shipping is `17f1446e5`; previous is the allocation
refactor from `64820c468`.

Default runtime, milliseconds:

| Scenario | Shipping | Previous | Concrete queues |
| --- | ---: | ---: | ---: |
| DenseContext | 86.09–94.02 | 60.06–84.11 | 31.76–33.60 |
| Dense | 41.35–41.90 | 24.16–27.85 | 22.68–24.14 |
| NoMatchContext | 15.22–15.62 | 12.25–13.22 | 10.99–11.04 |

Diagnostic with `DOTNET_TieredCompilation=0`, milliseconds:

| Scenario | Shipping (one run) | Previous | Concrete queues |
| --- | ---: | ---: | ---: |
| DenseContext | 33.39 | 32.82–34.14 | 27.53–28.00 |
| Dense | 21.89 | 24.03–42.33 | 19.01–19.09 |
| NoMatchContext | 10.72 | 10.93–11.21 | 9.32–9.50 |

DenseContext allocation fell from about 82.49 MB to 74.49 MB per invocation;
Dense fell from 59.28 MB to 55.28 MB. NoMatchContext remained about 9.63 MB.
These decimal MB figures include harness allocations. The reductions correspond
to two removed 40-byte enumerators per dense context result and one without
context, on this runtime.

Earlier traces showed substantial, uneven runtime profiling helper costs. Even
the tiering-disabled previous build varied between fresh processes. These runs
support retaining the queue change, but do not establish a portable speedup
factor or identify the cause of all remaining variability. First-result latency,
cold storage, large-file scaling and other workload performance need separate
validation. The first-extraction executable was not rerun in this experiment.

## Local artifacts and reproduction

Raw JSON and fixtures are under `tmp/SelectString/queues-default`,
`queues-default-repeat`, `queues-tiering-off` and `queues-tiering-off-repeat`.
The diagnostic environment override is recorded here; those JSON files do not
record runtime environment overrides. The initial failed comma-separated
`-File` invocation in the diagnostic directory is not included in results.json.

Utility assembly SHA256:

- Previous: `5F7275EA6F5DC6C4F44D3F536859D61F04693874B9F41855A2E17BBE1B38225F`
- Concrete queues: `15103D2D15FAAAF54A9787672B70F19DDE80651C21729CFBFF5C70CE16BC5B50`

From a PowerShell prompt at the repository root:

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe,./tmp/SelectString/concrete-queues/pwsh.exe `
    -Revision shipping,previous,concrete-queues `
    -Scenario DenseContext,Dense,NoMatchContext `
    -Iterations 25 -WarmupIterations 60 -Affinity 1 -SkipCounting `
    -Artifacts ./tmp/SelectString/queues-default
```

The `candidate` directory still contains the previous build for comparison;
use `concrete-queues` to test this change.
