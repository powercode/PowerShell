# Large-log ETW findings (2026-10-23)

Captures `20261023-115829` (LogAllMatches) and `20261023-120603` (LogSparse)
compared shipping and the queue-optimized candidate on the same 300 MiB fixture.
Both used 60 warmups, 25 measured calls, affinity 1, null redirection and default
runtime settings. All four traces have zero event/buffer loss and 25 paired
measured regions. Validation checked all result line numbers and named captures
before warmup. Counting was disabled during capture.

| Scenario/build | Median wall ms | Sum wall ms | Allocated MB/call | GC suspension ms |
| --- | ---: | ---: | ---: | ---: |
| AllMatches shipping | 534.30 | 13678.46 | 1541.30 | 602.09 |
| AllMatches candidate | 517.92 | 13460.41 | 1504.33 | 447.54 |
| Sparse shipping | 310.10 | 8204.12 | 709.84 | 326.57 |
| Sparse candidate | 310.76 | 8205.07 | 650.70 | 256.77 |

MB is decimal. GC suspension totals cover all measured regions, not one call.
Both scenarios had one Gen1 collection per build and no Gen2 collections.
AllMatches validated 924,366 result lines / 978,741 matches; Sparse validated 925.
The median AllMatches improvement is about 3%, but summed time improves only
1.6%. Sparse is effectively tied. These are diagnostic captures, not stable
speedup claims. See RESULTS-log.md for fixture and executable provenance.

## Remaining costs

Named regex leaf methods account for about 27% of candidate AllMatches CPU
samples. Sparse reduces regex/MatchCollection work while copying, searching for
line boundaries and UTF-8 decoding remain material. Kernel samples are about
21% for AllMatches and 31% for Sparse, including copying and page management;
leaf data alone cannot attribute all of this to file I/O. Inlining and unresolved
symbols limit component attribution. Count/class runtime profiling helpers are
negligible in these measured regions, unlike earlier DenseContext traces.

Neither scenario requests context. These measurements do not support prioritizing
another context-queue optimization. Larger reader buffers and compiled regex are
separate proposed experiments; preserve semantics and include setup costs in
end-to-end wall time before selecting a production policy.

## Highlighting explains an allocation discrepancy

Shipping creates two empty List<int> objects before matching each line when
highlighting is enabled by host virtual-terminal support. The candidate creates
them only for matching lines. For Sparse, saving two 32-byte lists for 923,441
nonmatching lines plus one 40-byte queue enumerator for each of 925 matches
predicts 59,137,224 bytes saved. The observed difference is 59,137,102.72 bytes,
within about 121 bytes of that prediction.

This is strong source-backed evidence, but the terminal capability flag was not
recorded. Do not attribute traced/untraced allocation differences solely to ETW:
host-dependent emphasis preparation also matters, even when output is discarded.
Future results should record terminal capabilities. NoEmphasis is a useful
explicit comparison, not a silent replacement for the default workload.

Raw ETLs remain under `tmp/SelectString/traces/<capture>/`. Local analysis files
are under `%TEMP%/SelectString-115829-analysis` and `SelectString-120603-analysis`.
Each contains findings.txt, summary.json, an analysis script, loss statistics,
CSV dumps and resolved native profiles. Native profiles span first measured begin
to last end; region GC/leaf analyses use individual pairs. Dumper schema warnings
remain distinct from event loss; managed address mapping is heuristic.
