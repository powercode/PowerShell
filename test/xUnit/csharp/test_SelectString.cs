// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
}
