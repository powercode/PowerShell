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

## Performance module and entry scripts

`SelectString.Performance.psd1` defines the module and its exports. Implementations,
parameter definitions and full help live in `SelectString.Performance.psm1`.
`Measure-SelectString.ps1`, `New-EtwCapture.ps1`, `New-LogFixture.ps1`, and
`test/SelectString/Compare-SelectString.ps1` load dynamic parameters from those definitions, force-reload
through `Initialize-Performance.ps1`, and delegate. Existing command lines still work.
The module owns build discovery, worker lifetime and directory completion classes;
completion uses `IArgumentCompleter` and `IArgumentCompleterFactory`.

For full help in a fresh interactive session, initialize first (PowerShell does not
load a dynamic script's help target just to display help):

```powershell
./test/perf/SelectString/Initialize-Performance.ps1
Get-Help Invoke-SelectStringMeasurement -Full
Get-Help ./test/perf/SelectString/New-EtwCapture.ps1 -Full
Get-Help New-SelectStringLogFixture -Full
Get-Help Invoke-SelectStringComparison -Full
```

The initializer imports globally for interactive use. It is deliberately separate
from the manifest's `ScriptsToProcess`: it imports the manifest itself. Ordinary
script execution and tab completion initialize automatically. Avoid `using module`
in wrappers so parse-time class references do not pin old definitions during edits.

Run the harness checks with a built executable:

```powershell
./test/SelectString/Test-PerformanceScripts.ps1 -PwshPath ./tmp/SelectString/candidate/pwsh.exe
```

These check help, completion, changed-class reload, worker failures/timeouts,
fixture validation and small real measurements. Capture orchestration uses a fake
recorder; the checks do not start WPR. Artifacts stay under `tmp/SelectString`.
Measurement JSON also records `HostSupportsVirtualTerminal` to help explain
host-dependent highlight allocations.

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

## Reader buffer experiment

`ReaderBufferScanning` compares fixed 1 KiB, 16 KiB and
64 KiB buffers in the complete engine file scan. It covers sparse captures and
all matches on the shared 300 MiB log, 256 small files, and a first-line match with
early termination. The FileStream configuration and regex options stay unchanged.
It requires the current engine (unset `SELECTSTRING_ENGINE_SOURCE`).

```powershell
./test/perf/SelectString/New-LogFixture.ps1  # only if the shared fixture is absent
$env:SELECTSTRING_LOG_FIXTURE = (Resolve-Path ./tmp/SelectString/log-fixture).Path
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*ReaderBufferScanning*' --job short --affinity 1 --artifacts tmp/SelectString/buffers-bdn
```

Setup checks the manifest/hash and validates result order and named captures for
each large-log buffer variant. File generation, validation and regex construction
are outside timing. MemoryDiagnoser reports scan allocations; the sink counts
without retaining results. These are warm-cache engine measurements, not shipping
cmdlet comparisons. Confirm a promising choice end-to-end before adoption.

Larger buffers can decode beyond an early match and encounter invalid bytes that
the default reader never reaches. A regression test demonstrates this with strict
UTF-8 decoding. Buffer size therefore affects observable early-exit behavior as
well as memory and throughput; an unconditional increase is not semantics-neutral.

### Compiled-regex experiment

`PatternFamilyScanning` tests literal text, `alpha.*omega`, `alpha.+omega`, SQL
durations, order IDs, image extensions, PR references, Markdown headings and secret
assignments. `PatternFamilies.cs` records source URLs and authored inputs with
expected spans and groups. Standalone tests cover both compilation/case settings
and both AllMatches settings; benchmark setup validates its own selected variant.
These catalog expressions are search workloads, not complete domain validators.

```powershell
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*PatternFamilyScanning*Records: 10000,*CaseSensitive: True,*Input: Mixed*' --job short --affinity 1 --artifacts tmp/SelectString/pattern-families
```

This selects 36 cases: nine families, interpreted/compiled, fresh/reused engines.
Each operation matches 10,000 prepared records. Mixed cycles equally through a
successful match, an absent prefix, a short near miss and a roughly 4 KiB near miss.
This deliberately exercises failure paths; it is not a measured real-world input
distribution. The enum also supports each input kind separately; Records accepts
1 or 10000 and CaseSensitive accepts both settings. Filter these dimensions to
keep runs focused. Whole-line anchors are preserved. There is no file/pipeline
overhead in these benchmarks; confirm promising cases end-to-end before adoption.

The focused controls in RESULTS-patterns.md can be repeated with:

```powershell
$filters = foreach ($family in 'Literal','DotStar','DotPlus') {
    foreach ($inputKind in 'Match','Absent','LongNearMiss') {
        "*PatternFamilyScanning.ConstructAndMatch*Records: 10000,*CaseSensitive: True,*Family: $family, Input: $inputKind*"
    }
}
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter $filters --job short --affinity 1 --artifacts tmp/SelectString/pattern-controls
```

End-to-end pipeline workloads are available through `Measure-SelectString.ps1`:

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 -PwshPath ./tmp/SelectString/candidate/pwsh.exe -Revision current -Scenario PipelineCaptures,PipelineStop,FileStop -RecordCount 10000 -MatchEvery 100 -WarmupIterations 60 -Iterations 25 -Artifacts ./tmp/SelectString/pipeline-current
```

`PipelineCaptures` feeds all records into one cmdlet invocation using named capture
groups. `RecordCount` controls input size and `MatchEvery` controls match spacing;
the first record matches. `PipelineStop` uses the same input and appends
`Select-Object -First 1`. `FileStop` searches the ordinary file fixture and appends
the same downstream command. Total time includes termination and cleanup; first
result is timed in a separate instrumented pass. Fixtures and capture validation
are outside timing. These options do not change the historical `Records` scenario.
JSON records the pipeline parameters and validated result count.

Use `-LatencyIterations 25` to repeat the separate instrumented counting pass.
`FirstResultMs` is the median, and `FirstResultSamplesMs` retains every observation
(including null for an invocation with no output). The default remains one call;
`-SkipCounting` skips every latency call. Latency sampling follows throughput
measurement and includes the counting sink's overhead, so it is not directly
comparable with the null-redirection total time. Repetition exposes outliers but
does not remove instrumentation bias. Result counts must agree across samples.

`RegexScanning` compares interpreted and compiled regexes with the same production
reader-buffer policy. `ConstructAndScan` creates a fresh engine on every timed
operation, including regex compilation and first-use JIT. `ReuseAndScan` uses an
engine warmed during setup. Neither benchmark changes the cmdlet's regex policy.
These measure engine work; cmdlet startup, binding and output costs are excluded.

The matrix includes sparse captures, all matches, three patterns, a literal regex,
first-match termination, 256 small files, and a single string. File sizes range
from 4 KiB to 300 MiB. String and small-file controls have fixed inputs: select
SizeKiB 4 for those cases to avoid redundant runs. Setup generates deterministic
logs and validates named captures outside timing; no existing fixture is needed.
The early case models the shared engine stop-after-first path, not all the
differences between the cmdlet's Quiet and List output modes.

```powershell
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*RegexScanning*SizeKiB: 4,*String*' '*RegexScanning*SizeKiB: 307200,*LogAllMatches*' '*RegexScanning*SizeKiB: 307200,*LogSparse*' --job short --affinity 1 --artifacts tmp/SelectString/regex-initial
```

Use the size sweep to investigate amortization before proposing a selection rule.
Short runs are exploratory. Confirm any promising choice with repeated end-to-end
shipping/current/experimental comparisons, including compilation in each timed
cmdlet invocation and preserving first-result measurements.

The construction-inclusive sweep used in RESULTS-regex.md is:

```powershell
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*RegexScanning.ConstructAndScan*SizeKiB: 4,*' '*RegexScanning.ConstructAndScan*SizeKiB: 1024,*Log*' '*RegexScanning.ConstructAndScan*SizeKiB: 16384,*Log*' '*RegexScanning.ConstructAndScan*SizeKiB: 307200,*Early*' '*RegexScanning.ConstructAndScan*SizeKiB: 16384,*Multiple*' '*RegexScanning.ConstructAndScan*SizeKiB: 16384,*Literal*' --job short --affinity 1 --artifacts tmp/SelectString/regex-sweep
```

### File-size sweep and candidate policy

`ReaderBufferFileSizes` generates 4 KiB, 64 KiB, 1 MiB and 16 MiB log fixtures
outside timing and validates captures for each variant. It needs no shared fixture:

```powershell
dotnet run -c Release --project test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -- --filter '*ReaderBufferFileSizes*' --job short --affinity 1 --artifacts tmp/SelectString/buffer-sizes
```

The candidate uses a 16 KiB reader for seekable files of at least 1 MiB when the
requested encoding is a runtime-provided UTF-8 encoding with replacement fallback
and the scan is not a List/Quiet early-exit scan. Other cases keep 1 KiB. BOM
detection remains enabled, so a BOM can still override the requested encoding.
Strict decoding, custom encoding types and custom fallbacks retain their original
read-ahead behavior. Explicit buffer arguments in the standalone benchmarks bypass
selection, keeping the experiment independent of the candidate policy.

The 1 MiB gate is a conservative candidate supported by the size sweep, not a
universal break-even point. See RESULTS-buffer-policy.md for end-to-end evidence.

## End-to-end command

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision shipping,candidate `
    -Iterations 25 -WarmupIterations 15 -Affinity 1
```

Every build/scenario gets a separate process with a timeout. Startup, module
loading, data preparation and warmups (three by default) are excluded. Throughput samples
discard output with `& $action > $null`. A separate instrumented pass measures first-result
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
The [queue experiment](RESULTS-queues.md) and subsequent
[full workload validation](RESULTS-matrix.md) use the null-redirection harness.

## Focused Windows tracing

For the large-log scenarios, use the generation and capture commands in the
section below. Existing DenseContext captures keep their previous defaults.

Run `New-EtwCapture.ps1` from an elevated PowerShell after building shipping and
the candidate. Shipping uses `tmp/SelectString/baseline/pwsh.exe`. Pass the build
directory name under `tmp/SelectString` with `-CandidateDirectory` (tab completion
is supported). When omitted, the newest eligible build is selected by the Utility
assembly modification time, falling back to `pwsh.exe` modification time if the
assembly is absent. Ties are resolved by directory name. Baseline, first-extraction,
the old experiment and shipping directories are excluded from automatic
selection/completion. Results and traces always use the labels `shipping` and
`candidate`, independently of the selected directory;
the selected executable path is printed before recording. The extraction
build is no longer part of these captures. Run from the repository root:

```powershell
./test/perf/SelectString/New-EtwCapture.ps1 -CandidateDirectory candidate
```

It writes separate DenseContext traces and benchmark JSON
under `tmp/SelectString/traces/<timestamp>` and retains WPR stop logs. Do not run
it alongside another recording. Reported event loss stops further captures.

`SelectString.wprp` captures system CPU samples with stacks and process/image
events. CLR events are filtered to `pwsh.exe`, keyword mask `0x19` (GC, loader,
JIT), at verbose level with allocation-tick event ID 10 excluded. This includes
GC/pause activity, JIT starts and method-load metadata, but excludes heap dumps and
inlining diagnostics. It does not attribute allocations by type. See the
[runtime event manifest](https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/ClrEtwAll.man)
for event levels and keywords. Each collector has 256 buffers of 256 KiB.

Recording begins before worker startup to capture method loads and includes
warmup. The capture script enables `-TraceMarkers` and `-SkipCounting` so the
ForEach-Object latency/count pass does not contaminate CPU captures. A plain for
loop drives warmup. Ordinary measurement runs still include the separate counting
pass unless explicitly skipped; skipped latency/count fields are null.

`BenchmarkEvents.cs` provides warmup, measured-iteration and counting boundaries.
Marker calls are outside the timing/allocation samples; iteration regions also
include the small measurement bookkeeping surrounding each timed call. The WPA
profile loads `SelectString.Regions.xml` to expose these phases. Select measured
iteration regions when comparing builds. Failed iterations may have an unmatched
begin marker and must not be treated as successful samples.

JSON records `Sink`, `WorkerPid`, `TraceMarkers` and `CountingMeasured`. Null
redirection discards streamed output without binding each object to a sink cmdlet.
Null assignment was also tried, but retained output and produced substantial
Gen 1/Gen 2 collection activity in dense scans. Historical reports used Out-Null
or NullAssignment: rerun all compared builds with this harness rather than
comparing new numbers directly against those old reports. Marker compilation and
registration happen before warmup and only when requested; untraced runs do not
load the provider. Actual ETW region pairing still needs validation in WPA.
Filter analysis to the benchmark worker PID; other PowerShell processes
also match the executable filter. Kernel sampling remains system-wide to expose
competing CPU activity. Managed symbol coverage and event loss must be checked
in the resulting trace. Traced timings are diagnostic, not benchmark claims.

Open traces with `tools/performance/SelectString.wpaProfile` for CPU stacks,
engine-oriented stack tags and GC/JIT regions. See that directory's README for
the opening command and interpretation limits.

## Large synthetic application log

See [RESULTS-log.md](RESULTS-log.md) for initial measurements and validation.

Log scenarios automatically reuse `tmp/SelectString/log-fixture`. If neither
the log nor its manifest exists, they generate the default 300 MiB fixture before
workers or recording start. An incomplete or invalid fixture still fails validation
rather than overwriting existing data. `-LogFixture` selects an explicit directory.

To generate a different size or location explicitly and reuse it across builds:

```powershell
./test/perf/SelectString/New-LogFixture.ps1 -SizeMiB 300

./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision shipping,candidate `
    -Scenario LogRare,LogSparse,LogMedium,LogBroad,LogDense,LogAllMatches,LogContext `
    -LogFixture ./tmp/SelectString/log-fixture `
    -WarmupIterations 3 -Iterations 5 -Affinity 1 -TimeoutSeconds 600 `
    -Artifacts ./tmp/SelectString/log-300-results
```

The default directory is `tmp/SelectString/log-fixture`; `-Directory` can select
another location. Generation overwrites `application.log` and `manifest.json` in
that directory. The file reaches at least the requested size, ending at a complete
line. The manifest records actual bytes, line count, generator version and SHA256.
The benchmark checks the hash before launching workers and never regenerates the
shared log. Use `-SizeMiB 1` and a separate directory for smoke checks.

Entries vary timestamp, worker, route, request ID, duration and message length
(including Unicode). A four-digit bucket supplies multiple densities in the same
file; queries capture named `level`, `request` and `duration` groups. Matching is
case-sensitive. Density refers to matching lines, not capture count:

| Scenario | Matching lines | Behavior |
| --- | ---: | --- |
| LogRare | 0.01% | Default detailed output |
| LogSparse | 0.1% | Default detailed output |
| LogMedium | 1% | Default detailed output |
| LogBroad | 10% | Default detailed output |
| LogDense | 100% | Default detailed output |
| LogAllMatches | 100% | Every seventeenth line has a second matching record |
| LogContext | 0.1% | Two lines of pre/post context |

Percentages are approximate at a partial final bucket cycle. Bucket selection is
regularly spaced; this is a reproducible synthetic workload, not evidence of
production representativeness. It does not model clustered error bursts, multiline
exceptions or cold storage. A representative anonymized log is still useful.

Before warmup, each worker streams a validation pass checking every result's line
number, match count and named capture values against arithmetic expectations.
Validation failures prevent a successful measurement. `ValidatedResults` and
`ValidatedMatches` distinguish line counts from multiple regex matches. Existing
differential tests remain the checks for complete context semantics. Validation
also runs with `-SkipCounting`, outside measured ETW regions; it warms the cache
and exercises the runtime before explicit warmup. Add-Type compilation is outside
timing as well.

JSON includes the fixture manifest, MiB/s, allocation/GC samples and optional
first-result latency. Peak working set includes setup, validation and counting;
it is not incremental scan memory. Five calls/three explicit warmups are an
initial exploration, not a stable performance claim; repeat comparisons and
inspect runtime tiering before drawing conclusions.

For a focused capture, run from elevated PowerShell after generation:

```powershell
./test/perf/SelectString/New-EtwCapture.ps1 `
    -Scenario LogContext `
    -WarmupIterations 3 -Iterations 5 -TimeoutSeconds 600
```

Choose another log scenario to vary density or exercise AllMatches. The script
captures shipping and the selected candidate using the same file, with
markers enabled and counting disabled. ETL filenames include the scenario.
Select the measured regions (five for this example); validation and startup are
also in the recording and must be excluded from timed-region analysis.

See [RESULTS-traces.md](RESULTS-traces.md) for the large-log ETW findings and the host-dependent highlighting allocation difference.

### Capturing multiple scenarios

`New-EtwCapture.ps1 -Scenario` accepts an array:

```powershell
./test/perf/SelectString/New-EtwCapture.ps1 -Scenario LogSparse,LogAllMatches
```

For each scenario, shipping and candidate are recorded separately, in that order.
The shared log is resolved/validated once before recording. Duplicate scenarios
run once. A batch writes each scenario beneath its own directory in the capture
folder, retaining separate ETLs, stop logs and benchmark JSON. Single-scenario
runs retain their existing directory layout. Recording or measurement failure
stops the batch after saving the active trace when possible.

### Pattern families in actual cmdlet invocations

`PatternFile` scans a generated UTF-8 file; `PatternRecords` feeds the same lines
through one pipeline invocation. Both use the corpus in `PatternFamilies.cs`,
shared with the standalone benchmarks, and include per-invocation regex setup.
Generation, Add-Type, validation and warmup are outside timing. Validation checks
all returned line numbers, order, spans and capture groups against authored
expectations. Output is discarded during throughput measurement.

```powershell
./test/perf/SelectString/Measure-SelectString.ps1 `
    -PwshPath ./tmp/SelectString/baseline/pwsh.exe,./tmp/SelectString/candidate/pwsh.exe `
    -Revision shipping,current -Scenario PatternFile,PatternRecords `
    -PatternFamily DotStar -InputKind LongNearMiss -RecordCount 10000 `
    -CaseSensitive -Culture en-US -WarmupIterations 30 -Iterations 15 `
    -LatencyIterations 5 -Affinity 1 -TimeoutSeconds 300 `
    -Artifacts ./tmp/SelectString/pattern-dotstar-long
```

`PatternFamily` accepts Literal, DotStar, DotPlus, SqlDuration, OrderId,
FileExtension, PrReference, Markdown and Assignment. `InputKind` accepts Match,
Absent, NearMiss, LongNearMiss and Mixed. Mixed cycles equally through the four
inputs, including 4 KiB near misses; this is a diagnostic distribution, not a
claim about production traffic. RecordCount applies to both file and pipeline.
Case-insensitive matching is the default; Culture defaults to en-US and is set
before cmdlet construction in the isolated worker. The supplied ASCII fixtures
are not themselves comprehensive culture tests; engine tests separately exercise
Turkish I and preservation of construction-time culture.

JSON records the pattern, family, input kind, record count, case mode and actual
culture. An absent-only workload validates zero results and has null first-result
latency. Use a separate artifact directory for each parameter combination; reruns
replace the files in that directory. Repeat with reversed executable order before
interpreting small timing differences.

### Background compilation probe

This is a standalone, benchmark-only prototype; it does not change the cmdlet.
Build the benchmark project, then run the probe in separate processes:

```powershell
dotnet build test/perf/SelectString/SelectString.Engine.Benchmarks.csproj -c Release
$probe = './test/perf/SelectString/bin/Release/net11.0/SelectString.Engine.Benchmarks.dll'
dotnet $probe --promotion-probe ./tmp/SelectString/promotion-one-core 1
dotnet $probe --promotion-probe ./tmp/SelectString/promotion-unrestricted 0
```

The final argument is a Windows affinity mask; zero retains OS affinity.
Unrestricted affinity permits spare cores but does not pin specific physical cores.
Use different directories for repeats: the probe replaces its per-case JSON files.
Repeat in reversed affinity order before interpreting scheduling differences.

This is an exploratory measurement loop, not a BenchmarkDotNet job. It exercises
one and 10,000 records for literal, wildcard and SQL patterns, with successful,
absent-prefix and long-near-miss inputs. Each case rotates six modes through three
warmup and nine measured rounds: interpreted, compiled, synchronous compilation
after 128 records, immediate background constructor-only, immediate background
with first execution, and background first execution after 128 records. The 128
record gate is a control for this experiment, not a proposed production threshold.
The first-execution probe uses only the selected authored fixture input.

Each operation constructs its own engine. Background work captures construction
culture and publishes a whole replacement between matching calls. A shared single
worker slot rejects busy admissions without queuing. Completion suppresses work
that has not begun and joins work already running. Worker failures are observed;
the measurement fails if speculative compilation fails. Concurrency admission,
faults, draining, culture, retained captures and the delayed gate have unit tests.

JSON retains foreground and total wall time, process CPU, managed allocations,
first-result latency, admission/rejection, whether compilation started, promotion
record index and time observed after that matching call, and unused compilations.
Totals include draining the worker; no admitted work carries into the next call.
Process CPU is coarse, especially for sub-millisecond calls, and managed allocation
excludes native generated code. Foreground time deliberately excludes drain time;
it does not claim a cmdlet could abandon workers without lifecycle consequences.
Match counts are checked every call, with corpus span/capture validation before
measurement. These results omit pipeline/file/output costs and concurrent callers.
