# Select-String measurements

This suite separates the sequential search engine from the PowerShell adapter.
`SelectString.Engine.cs` is compiled directly into the standalone xUnit and
BenchmarkDotNet projects; neither references a PowerShell assembly. Keep that
file independent of PowerShell. Public result types, parameter binding, path
resolution, object conversion and pipeline output belong in `SelectString.cs`.

The first milestone preserves the shipped context algorithms and StreamReader
semantics. Result text owns its storage. Input strings are records, including
embedded newlines. File context resets for each file; pipeline context persists
between records. File scanning calls a synchronous sink and stops when it returns
false. `-List`/`-Quiet` wait for the necessary context and close the file before
emitting. Concurrency, pooled decoding, compiled regex selection and specialized
existence-only matching remain subsequent experiments.

## Build and check compatibility

Run from the repository root. Use separate worktrees and output directories to
avoid assembly reuse. The helper regenerates **both resources and types** in each
tree, publishes a framework-dependent Release build, copies reference assemblies
needed by Add-Type/Pester, and records build provenance.

```powershell
git worktree add --detach ../SelectString-baseline 17f1446e5c4d88636e0d65bfedbaf8fe1bd5aaa6

./test/SelectString/Build-Comparison.ps1 -SourceRoot ../SelectString-baseline -OutputDirectory ./tmp/SelectString/baseline
./test/SelectString/Build-Comparison.ps1 -SourceRoot . -OutputDirectory ./tmp/SelectString/candidate

dotnet test test/SelectString/SelectString.Engine.Tests.csproj -c Release
./test/SelectString/Compare-SelectString.ps1 -Baseline ./tmp/SelectString/baseline/pwsh.exe -Candidate ./tmp/SelectString/candidate/pwsh.exe

& ./tmp/SelectString/candidate/pwsh.exe -NoProfile -Command {
    Import-Module Pester -RequiredVersion 4.10.1
    $result = Invoke-Pester -Script ./test/powershell/Modules/Microsoft.PowerShell.Utility/Select-String.Tests.ps1 -PassThru
    if ($result.FailedCount) { exit 1 }
}
```

On Unix use `pwsh` instead of `pwsh.exe`. The existing Pester file uses Pester 4
discovery semantics. The differential script does not require Pester. It compares
public result APIs, output fields, captures, formatting, context, errors, warnings,
FileInfo argument transformation, filtering identity and file closure. Snapshot
files are written under `test/SelectString/bin/comparison` by default.

## Standalone engine

```powershell
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*LargeFile*' --job short --artifacts test/perf/SelectString/bin/bdn
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*PatternSetup*' --job short --artifacts test/perf/SelectString/bin/bdn-setup
```

Omit `--job short` for longer measurements. Filter by scenario name, or use `'*'`
for the full matrix. Scanning and pattern preparation are separate benchmarks.
File generation is outside the measured operation. The result sink counts matches
without retaining them. MemoryDiagnoser records allocations and collections.
The project pins a BenchmarkDotNet preview with .NET 11 support and keeps language
properties local to avoid copying XML namespaces from the repository's older
MSBuild props into generated benchmark projects.

## End-to-end command

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision 17f1446e5,candidate `
    -Iterations 7
```

Every build/scenario gets a separate process with a timeout. Startup, module
loading, data preparation and three warmups are excluded. Throughput samples
consume output with Out-Null. A separate instrumented pass measures first-result
latency and counts results. JSON includes all timing samples, process-wide managed
allocation deltas, GC counts, peak working set, runtime, hardware and assembly hash.
Peak working set includes setup and warmup; it is not incremental engine memory.
PowerShell harness overhead is included in allocations and command timing, so use
the standalone suite for small operation and allocation comparisons.

These are **warm-cache** tests. They cover strings, streamed records, 256 small
files, a 100,000-line file, a 4 MiB line, sparse/dense/missing matches, multiple
patterns, context, early/late termination, output modes and several encodings.
Use `-LineCount` to scale the file workloads; fixture generation streams to disk
and the record-input workload retains at most 10,000 records. The default run
does not establish cold-storage performance or multi-gigabyte scalability.
The JSON preserves failures/timeouts rather than reporting them as fast results.
Counts alone do not establish correctness: use the differential comparison, and
exclude incorrect scenarios from speedup claims. Formatting is excluded from the
search throughput scenarios; `Formatting` measures context rendering separately.

See [RESULTS.md](RESULTS.md) for the initial measurements and limitations.
