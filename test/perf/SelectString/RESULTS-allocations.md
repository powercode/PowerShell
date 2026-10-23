# Sequential allocation phase

Measured 2026-10-23 against shipping source
`17f1446e5c4d88636e0d65bfedbaf8fe1bd5aaa6` and the first extraction
`41b3169b4ca8bdcd6b93a4c3087d780b05b368f4`. Candidate is the allocation refactor,
measured before committing; source hashes are recorded below. All executables are
framework-dependent Release builds with generated resources and type catalogs.

## Change and compatibility

Matching returns value-type metadata. A constrained struct factory creates the
final detailed result directly and gives the context trackers access to that same
object. The core remains independent of PowerShell. Logical context entries are
value types instead of one allocated object per input line. Raw and boolean Quiet
retain lightweight results; existence-only matching is deferred.

Validation: 28 standalone tests, 56 existing Pester tests (4.10.1), and 138
differential comparisons with shipping passed. New coverage includes caller-owned
result identity, overlapping context, ring wraparound, EOF, cancellation, filtering
and downstream mutation. Direct result sharing also restores shipped behavior
when downstream code changes a match's Line before it becomes later pre-context.
All 20 end-to-end scenarios returned identical result counts across the builds.

## Allocation results

Windows 11, Intel Core i9-13900K, x64, SDK `11.0.100-rc.2.26504.105`, runtime
`11.0.0-rc.2.26504.105`. Warm-cache fixtures: 100,000 lines, 256 small files and a
4 MiB line. Separate processes per build/scenario; 15 warmups and 25 measured
calls, affinity mask 1. No other benchmark job ran concurrently.

Mean managed allocation per invocation, including PowerShell harness overhead;
MiB = 1,048,576 bytes. Raw samples, GC counts, first-result latency, peak working
set and executable hashes are in the ignored `bin/phase2-affinity/results.json`.
Peak working set includes setup and warmup and is not retained engine memory.

| Scenario | Shipping MiB | First extraction MiB | Candidate MiB |
|---|---:|---:|---:|
| Dense | 100.786 | 107.653 | 100.786 |
| AllMatches | 126.038 | 132.904 | 126.038 |
| Context | 13.461 | 13.576 | 10.408 |
| DenseContext | 125.971 | 137.415 | 122.920 |
| NoMatchContext | 12.239 | 12.239 | 9.186 |
| LargeFile | 10.128 | 10.196 | 10.128 |
| Records | 10.899 | 10.906 | 10.899 |
| SmallFiles | 4.215 | 4.289 | 4.273 |
| Raw | 10.126 | 10.103 | 10.095 |
| List | 3.854 | 3.938 | 3.922 |

Dense detailed output removes approximately 72 bytes per match introduced by the
first extraction, returning to shipping allocation levels. Context additionally
eliminates the intermediate context object and per-line context entries. The
no-match context case saves about 3.05 MiB for 100,000 lines, matching removal of
32 bytes per line. Small-file/List overhead remains a separate optimization target.

The independent BenchmarkDotNet no-match context run also measured 12.21 MB for
the first engine and 9.16 MB for the candidate. Both used the same benchmark
source, regex mode, MediumRun (two launches, ten warmups, fifteen measurements),
without affinity. Timing varied substantially between launches; these runs
corroborate allocation savings, not a throughput claim. Reports are under
`bin/phase2-bdn-old/results` and `bin/phase2-bdn-new/results`.

## Timing interpretation

The initial pinned run was not steady-state for dense output: samples changed
sharply during measurement. For example, the first extraction's Dense samples
fell from roughly 290 ms to 140 ms, and the candidate's from roughly 290 ms to
200 ms. Its medians (144.66 and 209.15 ms) must not be presented as a stable
regression estimate. Unpinned runs also varied across process launches. More
warmup and a reversed build order were used for a focused follow-up.

That follow-up used 60 warmups, 25 measurements and the same affinity mask:

| Scenario | First extraction median ms | Candidate median ms |
|---|---:|---:|
| Dense | 119.65 | 56.81 |
| AllMatches | 93.68 | 135.03 |
| DenseContext | 70.71 | 165.75 |
| NoMatchContext | 12.64 | 12.92 |

Dense reverses the earlier ranking, while AllMatches and DenseContext remain
slower in this run. The unpinned focused run had Dense at 52.43 versus 50.60 ms
and DenseContext at 58.54 versus 56.74 ms (first extraction versus candidate).
These conflicting observations leave throughput validation open. Allocation
savings are established; a stable speedup or absence of regressions is not.
Before further optimization, use controlled repeated process launches and inspect
runtime warmup/tiering and profiles, especially for dense context. Do not average
these different runs into a single speedup number.

## Reproduction

Build the three revisions using the README's comparison helper, then run:

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/first-extraction/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision 17f1446e5,41b3169b4,phase2 `
    -Iterations 25 -WarmupIterations 15 -Affinity 1 `
    -Artifacts test/perf/SelectString/bin/phase2-affinity

./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/candidate/pwsh.exe,./tmp/SelectString/first-extraction/pwsh.exe `
    -Revision phase2,41b3169b4 `
    -Scenario Dense,AllMatches,DenseContext,NoMatchContext `
    -Iterations 25 -WarmupIterations 60 -Affinity 1 `
    -Artifacts test/perf/SelectString/bin/phase2-affinity-repeat
```

The README documents selecting the first extraction's source for independent
engine benchmarks. Local raw artifacts are ignored; these measurements do not
establish cold-storage performance, multi-gigabyte scalability or concurrency gains.

Candidate SHA256:

```text
SelectString.cs        C1F02E145A6120B44D381F83CBA3B14DFD540FC18E3C6F65E80D54ACF5324850
SelectString.Engine.cs 168A20B7E07B60E8E08AEAA43D21ED4E6C33F8E8BC9F8193BA3ECA82D55AE042
Utility assembly      5F7275EA6F5DC6C4F44D3F536859D61F04693874B9F41855A2E17BBE1B38225F
```
