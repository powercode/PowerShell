// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.SelectString;

public sealed class SelectStringEngineTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 512)]
    [InlineData(true, 512)]
    public void SlowFirstFileAndConsumerBoundReadAheadAndAdmission(bool stopEarly, int batchSize)
    {
        WithFile(first => WithFile(second => WithFile(third =>
        {
            int lineCount = batchSize * 5 + 20;
            foreach (string path in new[] { first, second, third })
            {
                File.WriteAllLines(path, Enumerable.Repeat("hit", lineCount));
            }

            using var laterFull = new ManualResetEventSlim();
            using var firstFull = new ManualResetEventSlim();
            var started = new ConcurrentDictionary<string, bool>();
            var waits = new ConcurrentDictionary<string, ulong>();
            var observer = new ScanObserver(path =>
            {
                started[path] = true;
                // Hold the earliest scanner until a later file fills its own queue.
                // This would deadlock if later output consumed the earliest quota.
                if (path == first) { Assert.True(laterFull.Wait(TimeSpan.FromSeconds(10))); }
            }, (path, lines) =>
            {
                waits[path] = lines;
                if (path == second) { laterFull.Set(); }
                if (path == first && lines == 1UL + 2UL * (ulong)batchSize) { firstFull.Set(); }
            });
            var output = new List<string>();
            new SelectStringEngine(new[] { "hit" }).ScanFiles(new[] { first, second, third }, Encoding.UTF8,
                result =>
                {
                    output.Add(result.Path);
                    if (output.Count == 1)
                    {
                        // With one result held by this consumer, the first worker may
                        // scan two batches more. The later worker has no consumer yet:
                        // its first one-result batch and one full batch fill its queue.
                        Assert.True(firstFull.Wait(TimeSpan.FromSeconds(10)));
                        Assert.Equal(1UL + 2UL * (ulong)batchSize, waits[first]);
                        Assert.Equal(1UL + (ulong)batchSize, waits[second]);
                        Assert.False(started.ContainsKey(third));
                        Assert.Equal(first, result.Path);
                    }

                    return !stopEarly;
                }, (_, error) => throw error, concurrency: 2, queuedBatches: 2,
                maxResults: batchSize, byteTarget: batchSize * 1024L, observer: observer);

            if (stopEarly) { Assert.Single(output); }
            else
            {
                Assert.Equal(Enumerable.Repeat(first, lineCount).Concat(Enumerable.Repeat(second, lineCount))
                    .Concat(Enumerable.Repeat(third, lineCount)), output);
            }

            foreach (string path in new[] { first, second, third })
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        })));
    }

    private sealed class ScanObserver(Action<string> starting, Action<string, ulong> waiting) : SelectStringEngine.IScanObserver
    {
        public void Starting(string path) => starting(path);

        public void WaitingForCapacity(string path, ulong linesRead) => waiting(path, linesRead);
    }

    [Fact]
    public void ParallelFilesPreserveOrderDuplicatesContextAndCallbackThread()
    {
        WithFile(first => WithFile(second =>
        {
            File.WriteAllLines(first, new[] { "before", "hit 1", "hit 2", "after" });
            File.WriteAllText(second, "hit 3");
            string[] paths = { first, second, first };
            var engine = new SelectStringEngine(new[] { @"hit (\d)" }, compiledRegex: true);
            var expected = new List<string>();
            var actual = new List<string>();
            string Describe(SelectStringEngine.Result result) =>
                $"{result.Path}:{result.LineNumber}:{result.Matches[0].Groups[1].Value}:" +
                string.Join("|", result.Context.PreContext) + ":" + string.Join("|", result.Context.PostContext);
            foreach (string path in paths)
            {
                engine.ScanFile(path, Encoding.UTF8, 1, 1, false, result =>
                {
                    expected.Add(Describe(result));
                    return true;
                });
            }

            int caller = Environment.CurrentManagedThreadId;
            engine.ScanFiles(paths, Encoding.UTF8, result =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                actual.Add(Describe(result));
                return true;
            }, (_, error) => throw error, preContext: 1, postContext: 1, maxResults: 1);
            Assert.Equal(expected, actual);
        }));
    }

    [Fact]
    public void ParallelFileErrorsFollowEarlierResultsAndAllowLaterFiles()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "hit");
            var events = new List<string>();
            int caller = Environment.CurrentManagedThreadId;
            new SelectStringEngine(new[] { "hit" }).ScanFiles(
                new[] { path, path + ".missing", path }, Encoding.UTF8,
                result => { events.Add(result.Line); return true; },
                (failedPath, error) =>
                {
                    Assert.Equal(caller, Environment.CurrentManagedThreadId);
                    Assert.Equal(path + ".missing", failedPath);
                    Assert.IsType<FileNotFoundException>(error);
                    events.Add("error");
                });
            Assert.Equal(new[] { "hit", "error", "hit" }, events);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelStopAndSinkFailureJoinWorkersAndCloseFiles(bool throwFromSink)
    {
        WithFile(path =>
        {
            File.WriteAllLines(path, Enumerable.Repeat("hit", 10000));
            var engine = new SelectStringEngine(new[] { "hit" });
            int count = 0;
            void Scan() => engine.ScanFiles(new[] { path, path, path }, Encoding.UTF8,
                _ =>
                {
                    count++;
                    if (throwFromSink) { throw new InvalidOperationException("sink"); }
                    return false;
                }, (_, error) => throw error, queuedBatches: 1, maxResults: 1);
            if (throwFromSink) { Assert.Throws<InvalidOperationException>(Scan); }
            else { Scan(); }
            Assert.Equal(1, count);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Fact]
    public void ParallelCancellationFromConsumerStopsAllWorkers()
    {
        WithFile(path =>
        {
            File.WriteAllLines(path, Enumerable.Repeat("hit", 10000));
            using var cancellation = new CancellationTokenSource();
            int count = 0;
            Assert.ThrowsAny<OperationCanceledException>(() =>
                new SelectStringEngine(new[] { "hit" }).ScanFiles(new[] { path, path }, Encoding.UTF8,
                    _ =>
                    {
                        count++;
                        cancellation.Cancel();
                        return true;
                    }, (_, error) => throw error, queuedBatches: 1, maxResults: 1,
                    cancellationToken: cancellation.Token));
            Assert.Equal(1, count);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Fact]
    public void BatchesPauseScanningAndPublishFirstResultPromptly()
    {
        WithFile(path =>
        {
            File.WriteAllLines(path, Enumerable.Repeat("hit", 10));
            using var scan = new SelectStringEngine(new[] { "hit" }).OpenFileScan(path, Encoding.UTF8,
                maxResults: 3, byteTarget: long.MaxValue);
            var first = scan.ReadBatch();
            Assert.Equal(1, first.Count);
            Assert.Equal(1UL, scan.LinesRead);
            var next = scan.ReadBatch();
            Assert.Equal(3, next.Count);
            Assert.Equal(4UL, scan.LinesRead);
            Assert.Equal(1UL, first.Results[0].LineNumber);
            Assert.Equal("hit", first.Results[0].Matches[0].Value);
            scan.Dispose();
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<ObjectDisposedException>(() => scan.ReadBatch());
        });
    }

    [Fact]
    public void ByteTargetDoesNotTruncateOversizedResults()
    {
        WithFile(path =>
        {
            string huge = "hit" + new string('x', 100000);
            File.WriteAllLines(path, new[] { "hit", huge, "hit" });
            using var scan = new SelectStringEngine(new[] { "hit" }).OpenFileScan(path, Encoding.UTF8,
                maxResults: 32, byteTarget: 1024);
            Assert.Equal(1, scan.ReadBatch().Count);
            var batch = scan.ReadBatch();
            Assert.Equal(1, batch.Count);
            Assert.Equal(huge, batch.Results[0].Line);
            Assert.True(batch.EstimatedBytes > 1024);
            Assert.Equal(2UL, scan.LinesRead);
            Assert.Equal(3UL, scan.ReadBatch().Results[0].LineNumber);
            Assert.Null(scan.ReadBatch());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchesPreserveContextCapturesAndBom(bool inverted)
    {
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            WithFile(path =>
            {
                File.WriteAllText(path, "hit café\r\nhit café\rmiss\nhit café\nlast", encoding);
                var engine = new SelectStringEngine(new[] { "hit (?<word>café)" }, allMatches: true, notMatch: inverted);
                var expected = new List<SelectStringEngine.Result>();
                engine.ScanFile(path, Encoding.ASCII, 2, 2, false, item => { expected.Add(item); return true; });
                using var scan = engine.OpenFileScan(path, Encoding.ASCII, 2, 2, maxResults: 2);
                var actual = new List<SelectStringEngine.Result>();
                SelectStringEngine.ScanBatch batch;
                while ((batch = scan.ReadBatch()) != null)
                {
                    actual.AddRange(batch.Results.Take(batch.Count));
                }

                Assert.Equal(expected.Count, actual.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].LineNumber, actual[i].LineNumber);
                    Assert.Equal(expected[i].Line, actual[i].Line);
                    Assert.Equal(expected[i].Matches.Select(m => m.Value), actual[i].Matches.Select(m => m.Value));
                    Assert.Equal(expected[i].Context.PreContext, actual[i].Context.PreContext);
                    Assert.Equal(expected[i].Context.PostContext, actual[i].Context.PostContext);
                    Assert.Equal(expected[i].Context.DisplayPreContext, actual[i].Context.DisplayPreContext);
                    Assert.Equal(expected[i].Context.DisplayPostContext, actual[i].Context.DisplayPostContext);
                    if (!inverted) { Assert.Equal("café", actual[i].Matches[0].Groups["word"].Value); }
                }
            });
        }
    }

    [Fact]
    public void CancellationBetweenBatchesClosesTheReader()
    {
        WithFile(path =>
        {
            File.WriteAllLines(path, ["hit", "miss", "miss"]);
            using var cancellation = new CancellationTokenSource();
            using var scan = new SelectStringEngine(["hit"]).OpenFileScan(path, Encoding.UTF8,
                cancellationToken: cancellation.Token);
            Assert.Equal(1, scan.ReadBatch().Count);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => scan.ReadBatch());
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Fact]
    public void CancellationDuringNoMatchScanDoesNotRequireOutput()
    {
        WithFile(path =>
        {
            File.WriteAllLines(path, Enumerable.Repeat("miss", 100));
            using var cancellation = new CancellationTokenSource();
            int checks = 0;
            using var scan = new SelectStringEngine.ScanSession<SelectStringEngine.Result, SelectStringEngine.ResultFactory>(
                new(["hit"]), path, Encoding.UTF8, 0, 0, false, default,
                shouldStop: () =>
                {
                    if (++checks == 3) { cancellation.Cancel(); }
                    return false;
                }, cancellationToken: cancellation.Token);
            Assert.Throws<OperationCanceledException>(() => scan.TryRead(out _));
            Assert.Equal(3UL, scan.LinesRead);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Fact]
    public void StopObservedDuringFinalizationSuppressesPendingOutput()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "hit");
            int checks = 0;
            bool emitted = false;
            bool found = new SelectStringEngine(["hit"]).ScanFile(path, Encoding.UTF8, 0, 0, true,
                _ => { emitted = true; return true; }, shouldStop: () => ++checks == 2);
            Assert.True(found);
            Assert.False(emitted);
        });
    }

    [Fact]
    public void PartialBatchPrecedesDecoderFailure()
    {
        WithFile(path =>
        {
            const byte MalformedByte = 0xff;
            // Valid matches fit in the first reader block; malformed bytes are later.
            byte[] valid = Encoding.UTF8.GetBytes("hit\nhit\n" + new string('x', 32768));
            File.WriteAllBytes(path, valid.Concat([MalformedByte]).ToArray());
            using var scan = new SelectStringEngine(new[] { "hit" }).OpenFileScan(path,
                new UTF8Encoding(false, true));
            Assert.Equal(1, scan.ReadBatch().Count);
            var partial = scan.ReadBatch();
            Assert.Equal(1, partial.Count);
            Assert.Equal(2UL, partial.Results[0].LineNumber);
            Assert.Throws<DecoderFallbackException>(() => scan.ReadBatch());
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    [InlineData(65536)]
    public void ReaderBuffersPreserveDecodedLinesCapturesAndContext(int bufferSize)
    {
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            WithFile(path =>
            {
                string before = new string('x', bufferSize - 2) + "😀";
                string hit = "hit café 😀";
                File.WriteAllText(path, $"{before}\r\n{hit}\r\nafter", encoding);
                var results = new List<SelectStringEngine.Result>();
                // BOM detection must override the deliberately incorrect fallback encoding.
                new SelectStringEngine(["hit (?<word>café)"]).ScanFile(
                    path, Encoding.ASCII, 1, 1, false,
                    result => { results.Add(result); return true; }, readerBufferSize: bufferSize);
                var result = Assert.Single(results);
                Assert.Equal(2UL, result.LineNumber);
                Assert.Equal(hit, result.Line);
                Assert.Equal("café", Assert.Single(result.Matches).Groups["word"].Value);
                Assert.Equal([before], result.Context.PreContext);
                Assert.Equal(["after"], result.Context.PostContext);
            });
        }
    }

    [Fact]
    public void LargerReaderBuffersCanDecodePastAnEarlyMatch()
    {
        WithFile(path =>
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hit\n" + new string('x', 32768)).Concat(new byte[] { 0xff }).ToArray());
            var engine = new SelectStringEngine(["hit"]);
            var strict = new UTF8Encoding(false, true);
            Assert.True(engine.ScanFile(path, strict, 0, 0, true, _ => true));
            Assert.Throws<DecoderFallbackException>(() =>
                engine.ScanFile(path, strict, 0, 0, true, _ => true, readerBufferSize: 65536));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticBufferKeepsStrictDecodingReadAhead(bool stopAfterFirst)
    {
        WithFile(path =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes("hit\n" + new string('x', 2 * 1024 * 1024));
            bytes[8192] = 0xff;
            File.WriteAllBytes(path, bytes);
            int count = 0;
            Assert.True(new SelectStringEngine(["hit"]).ScanFile(path,
                new UTF8Encoding(false, true), 0, 0, stopAfterFirst,
                result => { count++; return false; }));
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public void AutomaticBufferPreservesCustomEncodingReadSize()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "hit\n" + new string('x', 2 * 1024 * 1024));
            Assert.True(new SelectStringEngine(["hit"]).ScanFile(path,
                new ShortReadEncoding(), 0, 0, false, _ => false));
        });
    }

    private sealed class ShortReadEncoding : UTF8Encoding
    {
        public override int GetMaxCharCount(int byteCount)
        {
            if (byteCount > 1024) { throw new InvalidOperationException("Custom encoding received a larger read buffer"); }
            return base.GetMaxCharCount(byteCount);
        }
    }

    [Fact]
    public void StringsAreRecordsAndRetainTheirStorage()
    {
        string text = "before\nmatch\nafter";
        var engine = new SelectStringEngine(["match"]);
        Assert.True(engine.TryMatch(text, out var result));
        Assert.Same(text, new SelectStringEngine.ResultFactory().Create(text, null, 1, in result, false).Line);
        Assert.Equal(7, Assert.Single(result.Matches).Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstMatchingPatternWinsAndAllCapturesSurvive(bool compiled)
    {
        var engine = new SelectStringEngine(["missing", "(?<word>a)+", "b"], allMatches: true, compiledRegex: compiled);
        Assert.True(engine.TryMatch("aa b a", out var result));
        Assert.Equal(1, result.PatternIndex);
        Assert.Equal(2, result.Matches.Length);
        Assert.Equal(2, result.Matches[0].Groups["word"].Captures.Count);
        Assert.Equal(5, result.Matches[1].Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegexCompilationPreservesCultureCaseAndPatternErrors(bool compiled)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var engine = new SelectStringEngine(["I"], compiledRegex: compiled);
            Assert.True(engine.TryMatch("ı", out _));
            Assert.False(engine.TryMatch("i", out _));
            Assert.False(new SelectStringEngine(["I"], caseSensitive: true, compiledRegex: compiled).TryMatch("ı", out _));
            var error = Assert.Throws<SelectStringEngine.InvalidPatternException>(() =>
                new SelectStringEngine(["ok", "["], compiledRegex: compiled));
            Assert.Equal(1, error.PatternIndex);
            Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NotMatchRequiresAllPatternsToFail()
    {
        var engine = new SelectStringEngine(["a", "b"], allMatches: true, notMatch: true);
        Assert.False(engine.TryMatch("b", out _));
        Assert.True(engine.TryMatch("c", out var result));
        Assert.Equal(0, result.PatternIndex);
        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData("tr-TR", "I", "ı", true)]
    [InlineData("tr-TR", "I", "i", false)]
    [InlineData("en-US", "I", "i", true)]
    public void SimpleMatchUsesTheSelectedCulture(string culture, string pattern, string text, bool expected)
    {
        var engine = new SelectStringEngine([pattern], simpleMatch: true,
            compareInfo: CultureInfo.GetCultureInfo(culture).CompareInfo);
        Assert.Equal(expected, engine.TryMatch(text, out _));
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    [InlineData("utf-32", true)]
    [InlineData("windows-1252", false)]
    public void DecodingAndBomDetectionMatchStreamReader(string encodingName, bool bom)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = encodingName == "utf-8" ? new UTF8Encoding(bom) : Encoding.GetEncoding(encodingName);
        string text = new string('x', 4095) + "é\r\n\r\nhit café\rhit fin\nhit end";
        WithFile(path =>
        {
            File.WriteAllText(path, text, encoding);
            var results = Scan(path, new SelectStringEngine(["hit"]), bom ? Encoding.ASCII : encoding);
            Assert.Equal(new ulong[] { 3, 4, 5 }, results.Select(r => r.LineNumber));
            Assert.Equal(new[] { "hit café", "hit fin", "hit end" }, results.Select(r => r.Line));
        });
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    public void ContextIncludesOverlappingMatchesAndTruncatesAtEof(int pre, int post)
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "zero\nhit one\nhit two\nthree");
            var results = Scan(path, new SelectStringEngine(["hit"]), Encoding.UTF8, pre, post);
            Assert.Equal(2, results.Count);
            Assert.Equal(pre == 0 ? [] : ["zero"], results[0].Context.PreContext);
            Assert.Equal(post == 0 ? [] : ["hit two", "three"], results[0].Context.PostContext);
            Assert.Equal(post == 0 ? [] : ["three"], results[1].Context.PostContext);
            Assert.Empty(results[0].Context.DisplayPostContext);
        });
    }

    [Fact]
    public void ContextDoesNotLeakBetweenFiles()
    {
        WithFile(path =>
        {
            var engine = new SelectStringEngine(["hit"]);
            File.WriteAllText(path, "old\nhit old");
            Scan(path, engine, Encoding.UTF8, 2, 2);
            File.WriteAllText(path, "hit new");
            var result = Assert.Single(Scan(path, engine, Encoding.UTF8, 2, 2));
            Assert.Empty(result.Context.PreContext);
            Assert.Empty(result.Context.PostContext);
        });
    }

    [Fact]
    public void StopAfterFirstClosesFileBeforeCallingSinkAndCollectsContext()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "hit\nafter\nhit again");
            int count = 0;
            new SelectStringEngine(["hit"]).ScanFile(path, Encoding.UTF8, 0, 1, true, result =>
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(new[] { "after" }, result.Context.PostContext);
                count++;
                return true;
            });
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public void SinkCanStopBeforeMalformedTailIsDecoded()
    {
        WithFile(path =>
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hit\n" + new string('x', 32768)).Concat(new byte[] { 0xff }).ToArray());
            int count = 0;
            new SelectStringEngine(["hit"]).ScanFile(path, new UTF8Encoding(false, true), 0, 0, false, result =>
            {
                count++;
                return false;
            });
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public void CancellationReleasesTheFile()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "hit\nhit\nhit");
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => new SelectStringEngine(["hit"]).ScanFile(
                path, Encoding.UTF8, 0, 0, false, result => { cancellation.Cancel(); return true; }, cancellationToken: cancellation.Token));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EmptyFilesAndLinesHaveNoSyntheticTrailingRecord(string text)
    {
        WithFile(path =>
        {
            File.WriteAllText(path, text);
            Assert.Equal(text.Length == 0 ? 0 : 1, Scan(path, new SelectStringEngine(["^$"]), Encoding.UTF8).Count);
        });
    }

    [Fact]
    public void VeryLongUnterminatedLineIsNotTruncated()
    {
        WithFile(path =>
        {
            string text = new string('x', 1024 * 1024) + "hit";
            File.WriteAllText(path, text);
            Assert.Equal(text, Assert.Single(Scan(path, new SelectStringEngine(["hit"]), Encoding.UTF8)).Line);
        });
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 3)]
    public void CallerOwnedResultsReceiveBothContextsAcrossRingWraparound(int pre, int post)
    {
        WithFile(path =>
        {
            string[] lines = Enumerable.Range(0, 50).Select(i => i % 7 < 2 ? $"hit {i}" : $"line {i}").ToArray();
            File.WriteAllLines(path, lines);
            var created = new List<OwnedMatch>();
            var emitted = new List<OwnedMatch>();
            var engine = new SelectStringEngine(["hit"]);
            engine.ScanFile<OwnedMatch, OwnedMatchFactory>(path, Encoding.UTF8, pre, post, false,
                new OwnedMatchFactory(created), result => { emitted.Add(result); return true; });

            int[] positions = Enumerable.Range(0, lines.Length).Where(i => i % 7 < 2).ToArray();
            Assert.Equal(positions.Length, emitted.Count);
            Assert.Equal(created.Count, emitted.Count);
            for (int i = 0; i < emitted.Count; i++)
            {
                var result = emitted[i];
                Assert.Same(created[i], result);
                Assert.Equal(lines[positions[i]], result.Line);
                Assert.Equal(lines.Skip(Math.Max(0, positions[i] - pre)).Take(Math.Min(pre, positions[i])), result.Before);
                Assert.Equal(lines.Skip(positions[i] + 1).Take(post), result.After);
                Assert.NotNull(result.DisplayBefore);
                Assert.NotNull(result.DisplayAfter);
                Assert.DoesNotContain(result.DisplayBefore, line => line.StartsWith("hit", StringComparison.Ordinal));
                Assert.DoesNotContain(result.DisplayAfter, line => line.StartsWith("hit", StringComparison.Ordinal));
            }

            // Subsequent scans must not reuse emitted result objects or their context arrays.
            var first = emitted[0];
            var before = first.Before;
            var after = first.After;
            File.WriteAllText(path, "hit replacement");
            engine.ScanFile<OwnedMatch, OwnedMatchFactory>(path, Encoding.UTF8, pre, post, false,
                new OwnedMatchFactory(created), _ => true);
            Assert.Equal("hit 0", first.Line);
            Assert.Same(before, first.Before);
            Assert.Same(after, first.After);
        });
    }

    [Fact]
    public void EmittedCallerOwnedContextSurvivesCancellation()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "before\nhit\nafter\nhit later\ntail");
            var created = new List<OwnedMatch>();
            OwnedMatch emitted = null;
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => new SelectStringEngine(["hit"])
                .ScanFile<OwnedMatch, OwnedMatchFactory>(path, Encoding.UTF8, 1, 1, false,
                    new OwnedMatchFactory(created), result =>
                    {
                        emitted = result;
                        cancellation.Cancel();
                        return true;
                    }, cancellationToken: cancellation.Token));
            Assert.Same(Assert.Single(created), emitted);
            Assert.Equal(new[] { "before" }, emitted.Before);
            Assert.Equal(new[] { "after" }, emitted.After);
            Assert.Equal(new[] { "before" }, emitted.DisplayBefore);
            Assert.Equal(new[] { "after" }, emitted.DisplayAfter);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    private sealed class OwnedMatch
    {
        internal string Line { get; init; }

        internal string[] Before { get; set; }

        internal string[] After { get; set; }

        internal string[] DisplayBefore { get; set; }

        internal string[] DisplayAfter { get; set; }
    }

    private readonly struct OwnedMatchFactory(List<OwnedMatch> created) : SelectStringEngine.IResultFactory<OwnedMatch>
    {
        public OwnedMatch Create(string line, string path, ulong lineNumber, in SelectStringEngine.MatchData match, bool context)
        {
            var result = new OwnedMatch { Line = line };
            created.Add(result);
            return result;
        }

        public string GetLine(OwnedMatch result) => result.Line;

        public bool HasContext(OwnedMatch result) => true;

        public void SetLogicalContext(OwnedMatch result, string[] before, string[] after)
        {
            result.Before = before;
            result.After = after;
        }

        public void SetDisplayContext(OwnedMatch result, string[] before, string[] after)
        {
            result.DisplayBefore = before;
            result.DisplayAfter = after;
        }
    }

    private static List<SelectStringEngine.Result> Scan(string path, SelectStringEngine engine, Encoding encoding, int pre = 0, int post = 0)
    {
        var results = new List<SelectStringEngine.Result>();
        engine.ScanFile(path, encoding, pre, post, false, result => { results.Add(result); return true; });
        return results;
    }

    private static void WithFile(Action<string> action)
    {
        string path = Path.GetTempFileName();
        try
        { 
            action(path); 
        }
        finally 
        {
             File.Delete(path);
        }
    }
}
