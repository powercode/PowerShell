// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// These shared Add-Type helpers retain their global names for PowerShell callers.
#pragma warning disable CA1050

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public static class SelectStringLogFixture
{
    private static readonly string[] Messages = {
        "request completed", "inventory reservation updated", "payment provider response received",
        "retrying upstream request", "customer café preference loaded", "order dispatched",
        "cache entry refreshed", "dependency timeout observed"
    };

    public const string RecordPattern = @"level=(?<level>INFO|WARN|ERROR) request=(?<request>[0-9a-f]{16}) duration=(?<duration>[0-9]+)ms";

    public static string Pattern(string mode) => @"bucket=" + (mode switch
    {
        "LogRare" => "0000", "LogSparse" or "LogContext" => @"\d000",
        "LogMedium" => @"\d{2}00", "LogBroad" => @"\d{3}0", _ => @"\d{4}"
    }) + " " + RecordPattern;

    public static string Level(long i) => i % 1000 == 0 ? "ERROR" : i % 10 == 0 ? "WARN" : "INFO";

    public static string Record(long i) => "bucket=" + (i % 10000).ToString("D4", CultureInfo.InvariantCulture) + " level=" + Level(i) + " request=" + i.ToString("x16", CultureInfo.InvariantCulture) + " duration=" + ((i * 37) % 5000).ToString(CultureInfo.InvariantCulture) + "ms";

    public static long Generate(string path, long bytes)
    {
        var encoding = new UTF8Encoding(false);
        long size = 0, lines = 0;
        using var writer = new StreamWriter(path, false, encoding, 65536);
        while (size < bytes)
        {
            long i = lines++;
            string line = new DateTime(2026, 1, 1).AddMilliseconds(i * 137).ToString("O", CultureInfo.InvariantCulture)
                + " service=checkout worker=" + (i % 32).ToString(CultureInfo.InvariantCulture) + " " + Record(i)
                + " route=/api/orders/" + (i % 4096).ToString(CultureInfo.InvariantCulture)
                + " message=\"" + Messages[i % Messages.Length] + "\"" + Items(i)
                + (i % 17 == 0 ? " retry " + Record(i) : string.Empty);
            writer.Write(line);
            writer.Write('\n');
            size += encoding.GetByteCount(line) + 1;
        }
        return lines;
    }

    private static string Items(long i)
    {
        var text = new StringBuilder();
        for (int n = 0; n < (i * 73) % 21; n++)
            text.Append(" item=sku").Append(((i * 31 + n * 17) % 100000).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append((n % 5 + 1).ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }
}

public sealed class SelectStringLogValidator
{
    private readonly string mode;

    private readonly long lines;

    private long next;

    public long Count { get; private set; }

    public long Captures { get; private set; }

    public SelectStringLogValidator(string mode, long lines) { this.mode = mode; this.lines = lines; }

    private long Step => mode == "LogRare" ? 10000 : mode == "LogMedium" ? 100 : mode == "LogBroad" ? 10 : mode == "LogAllMatches" || mode == "LogDense" ? 1 : 1000;

    public void Accept(ulong lineNumber, Match[] matches)
    {
        if (lineNumber != (ulong)(next + 1)) throw new InvalidDataException("Unexpected matched line: " + lineNumber);
        int expected = mode == "LogAllMatches" && next % 17 == 0 ? 2 : 1;
        if (matches.Length != expected) throw new InvalidDataException("Unexpected match count");
        foreach (var match in matches)
        {
            if (match.Groups["level"].Value != SelectStringLogFixture.Level(next)
                || match.Groups["request"].Value != next.ToString("x16", CultureInfo.InvariantCulture)
                || match.Groups["duration"].Value != ((next * 37) % 5000).ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Incorrect capture at line " + lineNumber);
            Captures++;
        }
        Count++;
        next += Step;
    }

    public void Complete()
    {
        if (next < lines || Count != (lines + Step - 1) / Step) throw new InvalidDataException("Missing results");
    }
}
