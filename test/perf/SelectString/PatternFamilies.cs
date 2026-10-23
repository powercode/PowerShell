// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PowerShell.Benchmarks.SelectString;

public enum SearchPatternFamily
{
    Literal,
    DotStar,
    DotPlus,
    SqlDuration,
    OrderId,
    FileExtension,
    PrReference,
    Markdown,
    Assignment,
}

public enum SearchInputKind
{
    Match,
    Absent,
    NearMiss,
    LongNearMiss,
    Mixed,
}

/// <summary>
/// Authored inputs with independently specified expected spans and group values.
/// Catalog patterns retain their anchors; source names identify the original pages.
/// </summary>
public sealed record SearchPatternFixture(string Pattern, string Source, SearchPatternFixture.Example[] Examples)
{
    public sealed record Example(string Input, string Value, int Index, string[] Groups);

    public static SearchPatternFixture Create(SearchPatternFamily family)
    {
        string longText = new('x', 4096);
        return family switch
        {
            SearchPatternFamily.Literal => Fixture("alpha", null,
                Hit("prefix alpha suffix", "alpha", 7), "ordinary text", "alphX", longText + "alphX"),
            SearchPatternFamily.DotStar => Fixture("alpha.*omega", null,
                Hit("alpha one omega two omega", "alpha one omega two omega", 0), "ordinary text", "alpha one omegX", "alpha" + longText + "omegX"),
            SearchPatternFamily.DotPlus => Fixture("alpha.+omega", null,
                Hit("alpha one omega two omega", "alpha one omega two omega", 0), "ordinary text", "alphaomega", "alpha" + longText + "omegX"),
            SearchPatternFamily.SqlDuration => Fixture(@"\bduration=(\d+(?:\.\d+)?)ms\b", "sql-log-duration",
                Hit("query duration=123.45ms done", "duration=123.45ms", 6, "123.45"), "ordinary text", "duration=123.45mx", "duration=" + new string('1', 4096) + "mx"),
            SearchPatternFamily.OrderId => Fixture(@"\bORD-\d{7,10}\b", "support-order-id",
                Hit("order ORD-1234567 ready", "ORD-1234567", 6), "ordinary text", "ORD-12345678901", longText + " ORD-12345678901"),
            SearchPatternFamily.FileExtension => Fixture(@"\.(jpg|jpeg|png|gif|bmp|webp|svg)$", "file-extension-image",
                Hit("images/photo.jpeg", ".jpeg", 12, "jpeg"), "ordinary text", "images/photo.jpegx", longText + ".jpegx"),
            SearchPatternFamily.PrReference => Fixture(@"#(\d{1,6})\b", "github-pr-reference",
                Hit("Fixes #12345 today", "#12345", 6, "12345"), "ordinary text", "Fixes #1234567", longText + " #1234567"),
            SearchPatternFamily.Markdown => Fixture(@"^(#{1,6})\s+(.+)$", "markdown-heading-atx",
                Hit("## Introduction", "## Introduction", 0, "##", "Introduction"), "ordinary text", "####### Heading", new string('#', 4096) + " Heading"),
            SearchPatternFamily.Assignment => Fixture("\\b(api[_-]?key|secret|token)\\s*=\\s*['\"][A-Za-z0-9_-]{16,}['\"]", "secret-assignment-detector",
                Hit("set api_key=\"abcDEF0123456789\"", "api_key=\"abcDEF0123456789\"", 4, "api_key"), "ordinary text", "api_key=\"abcDEF0123456789", "api_key=\"" + longText),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }

    private static Example Hit(string input, string value, int index, params string[] groups) => new(input, value, index, groups);

    private static SearchPatternFixture Fixture(string pattern, string slug, Example hit, params string[] misses)
    {
        var examples = new Example[misses.Length + 1];
        examples[0] = hit;
        for (int i = 0; i < misses.Length; i++)
        {
            examples[i + 1] = new(misses[i], null, -1, Array.Empty<string>());
        }

        return new(pattern, slug == null ? "Authored control" : "https://testregex.com/patterns/" + slug, examples);
    }

    public void Validate(Func<string, Match[]> matchInput)
    {
        foreach (Example example in Examples)
        {
            ValidateResult(example, matchInput(example.Input));
        }
    }

    public static void ValidateResult(Example example, Match[] matches)
    {
        bool matched = matches != null && matches.Length != 0;
        if (matched != (example.Value != null)) { throw new InvalidDataException("Unexpected match"); }
        if (!matched) { return; }
        if (matches.Length != 1) { throw new InvalidDataException("Unexpected match count"); }
        var match = matches[0];
        if (match.Index != example.Index || match.Value != example.Value || match.Groups.Count != example.Groups.Length + 1)
        {
            throw new InvalidDataException("Incorrect match span/groups");
        }

        for (int i = 0; i < example.Groups.Length; i++)
        {
            if (match.Groups[i + 1].Value != example.Groups[i]) { throw new InvalidDataException("Incorrect capture"); }
        }
    }
}
