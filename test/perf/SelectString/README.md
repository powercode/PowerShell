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

The allocation phase separates matching metadata (`MatchData`, a value type)
from result construction. A constrained struct factory lets scanning and both
context trackers use caller-owned reference objects. Detailed output uses
`MatchInfo` directly; the core does not reference that type. Raw and boolean Quiet
output retain a lightweight representation. Logical context entries are immutable
value types in the ring buffer, while matching results remain shared references.
Filtered MatchInfo objects preserve shipped identity/cloning behavior, including
downstream line mutations that affect later context. The factory owns context
assignment; results and context arrays must not be reused after emission.

## Build and check compatibility

Run from the repository root. Use separate worktrees and output directories to
avoid assembly reuse. The helper regenerates **both resources and types** in each
tree, publishes a framework-dependent Release build, copies reference assemblies
needed by Add-Type/Pester, and records build provenance.

```powershell
git worktree add --detach ../SelectString-baseline 17f1446e5c4d88636e0d65bfedbaf8fe1bd5aaa6
git worktree add --detach ../SelectString-first-extraction 41b3169b4

./test/SelectString/Build-Comparison.ps1 -SourceRoot ../SelectString-baseline -OutputDirectory ./tmp/SelectString/baseline
./test/SelectString/Build-Comparison.ps1 -SourceRoot ../SelectString-first-extraction -OutputDirectory ./tmp/SelectString/first-extraction
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

For a controlled core-only comparison, `SELECTSTRING_ENGINE_SOURCE` can select
the first extraction's engine while retaining the same benchmark code and data:

```powershell
$savedEngine = $env:SELECTSTRING_ENGINE_SOURCE
try {
    $env:SELECTSTRING_ENGINE_SOURCE = (Resolve-Path ../SelectString-first-extraction/src/Microsoft.PowerShell.Commands.Utility/commands/utility/SelectString.Engine.cs).Path
    dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*NoMatchContext*False*' --job medium --artifacts test/perf/SelectString/bin/core-first
} finally {
    $env:SELECTSTRING_ENGINE_SOURCE = $savedEngine
}
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*NoMatchContext*False*' --job medium --artifacts test/perf/SelectString/bin/core-current
```

## End-to-end command

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/first-extraction/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision 17f1446e5,41b3169b4,candidate `
    -Iterations 25 -WarmupIterations 15 -Affinity 1
```

Every build/scenario gets a separate process with a timeout. Startup, module
loading, data preparation and warmups (three by default) are excluded. Throughput samples
consume output with Out-Null. A separate instrumented pass measures first-result
latency and counts results. JSON includes all timing samples, process-wide managed
allocation deltas, GC counts, peak working set, runtime, hardware and assembly hash.
Peak working set includes setup and warmup; it is not incremental engine memory.
PowerShell harness overhead is included in allocations and command timing, so use
the standalone suite for small operation and allocation comparisons.

`-Affinity` is an optional processor affinity mask (zero leaves scheduling alone).
The example pins workers to logical processor zero on Windows; use a mask allowed
by your operating system, or omit it where process affinity is unsupported.
Pin all compared builds to the same processor, particularly on hybrid CPUs.
BenchmarkDotNet accepts the corresponding `--affinity 1` option. Affinity and
warmup count are recorded in the command results.

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
`DenseContext` and `NoMatchContext` isolate context costs at opposite match densities.

See [RESULTS.md](RESULTS.md) for the initial measurements and
[RESULTS-allocations.md](RESULTS-allocations.md) for the allocation phase.

## Focused Windows tracing

Run `New-EtwCapture.ps1` from an elevated PowerShell after building the three
comparison executables. It writes separate DenseContext traces and benchmark JSON
under `tmp/SelectString/traces/<timestamp>` and retains WPR stop logs. Do not run
it alongside another recording. Reported event loss stops further captures.

`SelectString.wprp` captures system CPU samples with stacks and process/image
events. CLR events are filtered to `pwsh.exe`, keyword mask `0x19` (GC, loader,
JIT), at verbose level with allocation-tick event ID 10 excluded. This includes
GC/pause activity, JIT starts and method-load metadata, but excludes heap dumps and
inlining diagnostics. It does not attribute allocations by type. See the
[runtime event manifest](https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/ClrEtwAll.man)
for event levels and keywords. Each collector has 256 buffers of 256 KiB.

Recording begins before worker startup to capture method loads, and includes
warmup and the final latency/count pass. Phase markers are not yet emitted;
whole-trace totals mix these phases. The counting pass uses ForEach-Object and
adds downstream pipeline work. Switching timed calls to null assignment and
adding phase markers are the next measurement step, not part of these results.
Filter analysis to the benchmark worker PID; other PowerShell processes
also match the executable filter. Kernel sampling remains system-wide to expose
competing CPU activity. Managed symbol coverage and event loss must be checked
in the resulting trace. Traced timings are diagnostic, not benchmark claims.

Open traces with `tools/performance/SelectString.wpaProfile` for CPU stacks,
engine-oriented stack tags and GC/JIT regions. See that directory's README for
the opening command and interpretation limits.
