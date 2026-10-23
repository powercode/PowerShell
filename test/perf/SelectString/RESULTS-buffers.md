# Reader buffer experiment

This records the fixed-buffer experiment at `b16cd5666`. The subsequent selection
policy and cmdlet comparisons are recorded in [RESULTS-buffer-policy.md](RESULTS-buffer-policy.md).

Measured 2026-10-23. This isolates reader buffering in the current standalone
engine; it is not a comparison against shipping Select-String. The cmdlet still
uses the original 1 KiB buffer. No compiled-regex or FileStream changes were made.

## Method

BenchmarkDotNet 0.16.0-preview.2; .NET SDK/runtime 11.0.100-rc.2.26504.105 /
11.0.0-rc.2.26504.105; Windows 11, Intel Core i9-13900K. Affinity mask 1,
Concurrent Workstation GC. Warm filesystem cache. The attempted high-performance
power-plan switch was unavailable; the existing system power plan was retained.

The shared UTF-8 log is 314,573,210 bytes / 924,366 lines, SHA256
`7B7A77BDCD687645D8B1297513F741A3178529008BBF98243DD8E12EC5F15380`.
Every variant verifies its hash and validates result order and named captures
before timing. Generation, validation, regex construction and warmup are excluded.
The timed operation includes opening/closing files, decoding, matching, result
construction and a counting sink, without retaining results or PowerShell output.

Initial matrix: one launch, three warmups, three measured iterations. Confirmation:
two launches, five warmups and ten measured iterations per launch for each large-log
case. Both runs tested buffers in ascending order; no reverse-order confirmation
or end-to-end cmdlet run has been done. Treat these as evidence for a candidate,
not sufficient evidence to change the default.

## Large-log confirmation

Mean wall time per scan. Error is BenchmarkDotNet's 99.9% confidence half-width.

| Scenario | 1 KiB | 16 KiB | 64 KiB |
| --- | ---: | ---: | ---: |
| All matches | 557.0 +/- 16.08 ms | 465.8 +/- 9.50 ms | 447.3 +/- 8.54 ms |
| Sparse matches | 300.4 +/- 6.50 ms | 225.0 +/- 9.36 ms | 209.9 +/- 11.08 ms |

Relative to 1 KiB, 16 KiB reduced mean time by 16.4% / 25.1%, and 64 KiB by
19.7% / 30.1%. The shorter first run showed the same direction. Large-file
allocations were nearly unchanged: approximately 1,145 MiB for all matches and
620 MiB for sparse matches. Buffer allocation is small relative to line/result
allocation in these full scans.

## Small inputs and early termination

Exploratory short-run means; timing intervals are wide, particularly for early
termination. Allocation differences are clearer than small timing differences.
SmallFiles opens 256 files containing a matching first line and 256 further
characters. Early opens one file with a first-line match and 100,000 further
characters, then stops after the match.

| Scenario | Buffer | Mean | Allocated per operation |
| --- | ---: | ---: | ---: |
| SmallFiles | 1 KiB | 22.44 ms | 2,156 KiB |
| SmallFiles | 16 KiB | 23.09 ms | 12,646 KiB |
| SmallFiles | 64 KiB | 27.18 ms | 49,519 KiB |
| Early | 1 KiB | 78.80 us | 7.90 KiB |
| Early | 16 KiB | 80.26 us | 48.88 KiB |
| Early | 64 KiB | 128.16 us | 192.92 KiB |

The 64 KiB small-file/early cases also recorded Gen2 collections. Increasing the
buffer for every file imposes a substantial allocation cost even for tiny files.

## Compatibility and decision

All 32 standalone tests pass. Added coverage checks BOM detection, UTF-8/UTF-16/
UTF-32, Unicode at buffer boundaries, captures and context for all three sizes.
Utility Release build and performance harness checks pass. The Utility build
needed its stale source-generator output removed before regenerating it; no
resource/type stubs were introduced.

A strict UTF-8 file with `hit` on its first line and an invalid byte after 32 KiB
exposes a behavior difference: the default reader returns its early match; a
64 KiB reader throws during decoding before emitting it. Thus an unconditional
buffer increase violates the existing early-termination/error behavior.

Keep the current default. Next buffering work should locate the file-size
break-even point, preserve small-file and early-exit behavior (including strict
and custom encodings), then compare a specific policy with unchanged candidate
and shipping executables in both run orders. Compiled regex remains an independent
experiment; these measurements do not support enabling it or combining changes.

## Reproduction and artifacts

See README's reader buffer experiment for fixture setup and the short-run command.
For confirmation, replace `--job short` with
`--warmupCount 5 --iterationCount 10 --launchCount 2`.
Local NuGet audit lookup was disabled for the run because the feed was unavailable;
repository restore settings were not changed.

Raw local artifacts (ignored):

- `tmp/SelectString/buffers-bdn` - all 12 exploratory cases.
- `tmp/SelectString/buffers-confirm` - six large-log confirmation cases.
- `tmp/SelectString/buffers-tests.log` - 32 engine tests.
- `tmp/SelectString/buffers-utility-build.log` - Utility build.
- `tmp/SelectString/buffers-harness-tests.log` - harness checks, fake WPR only.

Source SHA256:

- `SelectString.Engine.cs`: `90B549882AABD63B4E462AF726CB5EB59E13B9AD6F62A956BF2C7940EF949E31`
- `ReaderBuffer.Benchmarks.cs`: `1D4F5B6DB8038728B7BB407BB1F51C452AD3B29D944F47422F4FAE4E238C83F5`
- `LogFixture.cs`: `D467A25584CD5C83B831DDDE07C4BDFDB18A9EB7054EFCD7F0C98B7BD1D86AD7`
