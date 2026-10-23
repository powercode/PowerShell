// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
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
                File.WriteAllText(path, before + "\r\n" + hit + "\r\nafter", encoding);
                var results = new List<SelectStringEngine.Result>();
                // BOM detection must override the deliberately incorrect fallback encoding.
                new SelectStringEngine(new[] { "hit (?<word>café)" }).ScanFile(
                    path, Encoding.ASCII, 1, 1, false,
                    result => { results.Add(result); return true; }, readerBufferSize: bufferSize);
                var result = Assert.Single(results);
                Assert.Equal(2UL, result.LineNumber);
                Assert.Equal(hit, result.Line);
                Assert.Equal("café", Assert.Single(result.Matches).Groups["word"].Value);
                Assert.Equal(new[] { before }, result.Context.PreContext);
                Assert.Equal(new[] { "after" }, result.Context.PostContext);
            });
        }
    }

    [Fact]
    public void LargerReaderBuffersCanDecodePastAnEarlyMatch()
    {
        WithFile(path =>
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hit\n" + new string('x', 32768)).Concat(new byte[] { 0xff }).ToArray());
            var engine = new SelectStringEngine(new[] { "hit" });
            var strict = new UTF8Encoding(false, true);
            Assert.True(engine.ScanFile(path, strict, 0, 0, true, _ => true));
            Assert.Throws<DecoderFallbackException>(() =>
                engine.ScanFile(path, strict, 0, 0, true, _ => true, readerBufferSize: 65536));
        });
    }

    [Fact]
    public void StringsAreRecordsAndRetainTheirStorage()
    {
        string text = "before\nmatch\nafter";
        var engine = new SelectStringEngine(new[] { "match" });
        Assert.True(engine.TryMatch(text, out var result));
        Assert.Same(text, new SelectStringEngine.ResultFactory().Create(text, null, 1, in result, false).Line);
        Assert.Equal(7, Assert.Single(result.Matches).Index);
    }

    [Fact]
    public void FirstMatchingPatternWinsAndAllCapturesSurvive()
    {
        var engine = new SelectStringEngine(new[] { "missing", "(?<word>a)+", "b" }, allMatches: true);
        Assert.True(engine.TryMatch("aa b a", out var result));
        Assert.Equal(1, result.PatternIndex);
        Assert.Equal(2, result.Matches.Length);
        Assert.Equal(2, result.Matches[0].Groups["word"].Captures.Count);
        Assert.Equal(5, result.Matches[1].Index);
    }

    [Fact]
    public void NotMatchRequiresAllPatternsToFail()
    {
        var engine = new SelectStringEngine(new[] { "a", "b" }, allMatches: true, notMatch: true);
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
        var engine = new SelectStringEngine(new[] { pattern }, simpleMatch: true,
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
            var results = Scan(path, new SelectStringEngine(new[] { "hit" }), bom ? Encoding.ASCII : encoding);
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
            var results = Scan(path, new SelectStringEngine(new[] { "hit" }), Encoding.UTF8, pre, post);
            Assert.Equal(2, results.Count);
            Assert.Equal(pre == 0 ? Array.Empty<string>() : new[] { "zero" }, results[0].Context.PreContext);
            Assert.Equal(post == 0 ? Array.Empty<string>() : new[] { "hit two", "three" }, results[0].Context.PostContext);
            Assert.Equal(post == 0 ? Array.Empty<string>() : new[] { "three" }, results[1].Context.PostContext);
            Assert.Empty(results[0].Context.DisplayPostContext);
        });
    }

    [Fact]
    public void ContextDoesNotLeakBetweenFiles()
    {
        WithFile(path =>
        {
            var engine = new SelectStringEngine(new[] { "hit" });
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
            new SelectStringEngine(new[] { "hit" }).ScanFile(path, Encoding.UTF8, 0, 1, true, result =>
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
            new SelectStringEngine(new[] { "hit" }).ScanFile(path, new UTF8Encoding(false, true), 0, 0, false, result =>
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
            Assert.Throws<OperationCanceledException>(() => new SelectStringEngine(new[] { "hit" }).ScanFile(
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
            Assert.Equal(text.Length == 0 ? 0 : 1, Scan(path, new SelectStringEngine(new[] { "^$" }), Encoding.UTF8).Count);
        });
    }

    [Fact]
    public void VeryLongUnterminatedLineIsNotTruncated()
    {
        WithFile(path =>
        {
            string text = new string('x', 1024 * 1024) + "hit";
            File.WriteAllText(path, text);
            Assert.Equal(text, Assert.Single(Scan(path, new SelectStringEngine(new[] { "hit" }), Encoding.UTF8)).Line);
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
            var engine = new SelectStringEngine(new[] { "hit" });
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
            Assert.Throws<OperationCanceledException>(() => new SelectStringEngine(new[] { "hit" })
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
        try { action(path); }
        finally { File.Delete(path); }
    }
}
