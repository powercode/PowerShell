// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.Parallel;

public class SelectStringTests
{
    private readonly string _filePath;
    private static readonly SearchValues<byte> s_searchValues = SearchValues.Create([(byte)'\r', (byte)'\n']);

    public SelectStringTests()
    { 
         _filePath = Path.GetFullPath("war_and_peace.txt");
        if (!File.Exists(_filePath))
        {
            var text = new HttpClient().GetStringAsync("https://gutenberg.org/cache/epub/2600/pg2600.txt").Result;
            File.WriteAllText(_filePath, text, Encoding.UTF8);
        }
        
    }
    
    [Fact]
    public void LoopTest()
    {
        for (int i = 0; i < 100; i++)
        {
            TestRegexMemoryMappedFile();
        }
    }
    
    [Fact]
    public void TestRegexMemoryMappedFile()
    {
        using var mmf = MemoryMappedFile.CreateFromFile(_filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        int fileSize = (int)new FileInfo(_filePath).Length;
        unsafe
        {
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            ReadOnlySpan<byte> span = new(ptr, fileSize);
            var lineRanges = GetLineRanges(span);
            var pattern = new Regex(@"\b(Gutenberg|the)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var count = ProcessLines(ptr, fileSize, lineRanges, pattern);
            Assert.Equal(34834, count);
            //testOutputHelper.WriteLine(count.ToString());
        }
    }

    private static unsafe int ProcessLines(byte* ptr, int length, List<Range> lineRanges, Regex pattern)
    {
        
        var arrayPool = ArrayPool<char>.Create();
        var decoder = Encoding.UTF8.GetDecoder();
        int count = 0;
        foreach (var lineRange in lineRanges)
        {
            var line = new ReadOnlySpan<byte>(ptr, length)[lineRange];
            var res = ProcessLine(line, pattern, arrayPool, decoder);
            if (res > 0)
            {
                Interlocked.Add(ref count, res);
            }
        }

        return count;
    }

    private static int ProcessLine(ReadOnlySpan<byte> lineSpan, Regex pattern, ArrayPool<char> arrayPool, Decoder decoder)
    {
        if (lineSpan.Length <= 0)
        {
            return 0;
        }

        var maxChars = Encoding.UTF8.GetMaxCharCount(lineSpan.Length);
        var buffer = arrayPool.Rent(maxChars);
        try
        {
            var charSpan = buffer.AsSpan();
            var length = decoder.GetChars(lineSpan, charSpan, true);
            charSpan = charSpan[..length];
            int count = 0;
            foreach (var _ in pattern.EnumerateMatches(charSpan))
            {
                count++;
            }

            return count;
        }
        finally
        {
            arrayPool.Return(buffer);
        }
    }

    private static List<Range> GetLineRanges(ReadOnlySpan<byte> span)
    {
        var ranges = new List<Range>((int)span.Length / 60);
        int lineStart = 0;
        ReadOnlySpan<byte> newline = [(byte)'\r', (byte)'\n'];
        while (!span.IsEmpty)
        {
            int i = span.IndexOfAny(newline);
            if (i == -1)
            {
                // No more newlines, add the rest of the span as the last line
                ranges.Add(new Range(lineStart, lineStart + span.Length));
                break;
            }

            int lineEnd = lineStart + i;
            ranges.Add(new Range(lineStart, lineEnd));

            // Move past the newline characters
            if (span[i] == '\r' && i + 1 < span.Length && span[i + 1] == '\n')
            {
                lineEnd++;
                i++; // Skip the '\n' after '\r'
            }

            lineStart = lineEnd + 1;
            span = span[(i + 1)..];
        }

        return ranges;
    }

    [Fact]
    public void TestLineRanges()
    {
        var text = """
                   The Project Gutenberg eBook of War and Peace
                       
                   This ebook is for the use of anyone anywhere in the United States and
                   most other parts of the world at no cost and with almost no restrictions
                   whatsoever. You may copy it, give it away or re-use it under the terms
                   """;
        var bytes = Encoding.UTF8.GetBytes(text);
        var actualRanges = GetLineRanges(bytes.AsSpan());
        
        var strings = actualRanges.Select(r => Encoding.UTF8.GetString(bytes[r])).ToArray();
        
        Range[] expectedRanges = [new Range(0, 44), new Range(46, 50), new Range(52, 121), new Range(123, 195), new Range(197, 267)];
        
        Assert.Equal(expectedRanges, actualRanges);
    }

    [Fact]
    public void RunSelectStringOnWarAndPeace()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("$PSStyle.OutputRendering = 'Ansi'");
        ps.AddStatement();
        ps.Commands.AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.LiteralPath), _filePath)
            .AddParameter(nameof(SelectStringCommand.Pattern), @"\b(Gutenberg|the)\b")
            .AddParameter(nameof(SelectStringCommand.AllMatches));
        //ps.Commands.AddCommand("Out-String");
        var res = ps.Invoke<MatchInfo>();
        var errors = ps.Streams.Error.ReadAll();
        Assert.Empty(errors);
    }

    [Fact]
    public void ShouldOnlyReturnTheCaseSensitiveMatch()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("$PSStyle.OutputRendering = 'Ansi'");
        ps.AddStatement();
        ps.Commands.AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.CaseSensitive))
            .AddParameter(nameof(SelectStringCommand.Pattern), @"hello");
        string[] input = ["hello", "Hello"];
        var res = ps.Invoke<MatchInfo>(input);
        var match = Assert.Single(res);
        Assert.Equal("hello", match.ToString());
    }

    [Fact]
    public void AllMatch()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("$PSStyle.OutputRendering = 'Ansi'");
        ps.AddStatement();
        ps.Commands.AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.AllMatches))
            .AddParameter(nameof(SelectStringCommand.Pattern), @"l");
        ps.Commands.AddCommand("Out-String");
        string[] input = ["hello", "Hello", "goodbye"];
        var res = ps.Invoke<string>(input).First();
        var nl = Environment.NewLine;
        string expected = $"{nl}he\e[7ml\e[0m\e[7ml\e[0mo{nl}He\e[7ml\e[0m\e[7ml\e[0mo{nl}{nl}";
        Assert.Equal(expected, res);
    }
    
    [Fact]
    public void SimpleMatch()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("$PSStyle.OutputRendering = 'Ansi'");
        ps.AddStatement();
        ps.Commands.AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.SimpleMatch))
            .AddParameter(nameof(SelectStringCommand.Pattern), @"l");
        ps.Commands.AddCommand("Out-String");
        string[] input = ["hello", "Hello", "goodbye"];
        var res = ps.Invoke<string>(input).First();
        var nl = Environment.NewLine;
        string expected = $"{nl}he\e[7ml\e[0mlo{nl}He\e[7ml\e[0mlo{nl}{nl}";
        Assert.Equal(expected, res);
    }

    [Theory]
    [InlineData("string", "1:This is a text string, and another string")]
    [InlineData("second", "2:This is the second line")]    
    [InlineData("matches", "5:No matches")]

    public void MatchesLine(string pattern, string expectedEnd)
    {
        var nl = Environment.NewLine;
        var text = $"This is a text string, and another string{nl}This is the second line{nl}This is the third line{nl}This is the fourth line{nl}No matches";
        
        var file = Path.GetTempFileName();
        File.WriteAllText(file, text);
        
        using var ps = PowerShell.Create();
        ps.Commands.AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.LiteralPath), file)
            .AddParameter(nameof(SelectStringCommand.Pattern), pattern);
        
        var res = ps.Invoke<MatchInfo>().First();
        string expected = $"{file}:{expectedEnd}";
        Assert.Equal(expected, res.ToString());
    }
    
    [Theory]
    [InlineData("string", "1:This is a text string, and another string")]
    [InlineData("second", "2:This is the second line")]    
    [InlineData("matches", "5:No matches")]

    public void MatchesLineRelativePath(string pattern, string expectedEnd)
    {
        var nl = Environment.NewLine;
        var text = $"This is a text string, and another string{nl}This is the second line{nl}This is the third line{nl}This is the fourth line{nl}No matches";
        
        var file = Path.GetTempFileName();
        File.WriteAllText(file, text);
        var fileName = Path.GetFileName(file); 
        using var ps = PowerShell.Create();
        ps.Commands
            .AddCommand("Push-Location")
            .AddParameter(nameof(PushLocationCommand.LiteralPath), Path.GetDirectoryName(file))
            .AddStatement()
            .AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.LiteralPath), file)
            .AddParameter(nameof(SelectStringCommand.Pattern), pattern);
        
        var res = ps.Invoke<MatchInfo>();
        var first = Assert.Single(res);
        var actual = first.ToString(Path.GetDirectoryName(file));
        string expected = $"{fileName}:{expectedEnd}";
        Assert.Equal(expected, actual);
    }
}
