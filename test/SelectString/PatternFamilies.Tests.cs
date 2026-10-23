// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using Microsoft.PowerShell.Commands;
using PowerShell.Benchmarks.SelectString;
using Xunit;

namespace PSTests.SelectString;

public sealed class PatternFamiliesTests
{
    [Theory]
    [InlineData(false, "en-US", "tr-TR", true, false)]
    [InlineData(true, "en-US", "tr-TR", true, false)]
    [InlineData(false, "tr-TR", "en-US", false, true)]
    [InlineData(true, "tr-TR", "en-US", false, true)]
    public void MatchingRetainsConstructionCulture(bool compiled, string constructionCulture, string matchingCulture, bool latinI, bool dotlessI)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(constructionCulture);
            var insensitive = new SelectStringEngine(new[] { "^(I)(.*)$" }, compiledRegex: compiled);
            var sensitive = new SelectStringEngine(new[] { "^(I)(.*)$" }, caseSensitive: true, compiledRegex: compiled);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(matchingCulture);
            Assert.Equal(latinI, insensitive.TryMatch("input", out _));
            Assert.Equal(dotlessI, insensitive.TryMatch("\u0131nput", out _));
            Assert.True(insensitive.TryMatch("INPUT", out var match));
            Assert.Equal("I", match.Matches[0].Groups[1].Value);
            Assert.False(sensitive.TryMatch("input", out _));
            Assert.False(sensitive.TryMatch("\u0131nput", out _));
            Assert.True(sensitive.TryMatch("INPUT", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void FixturesHaveExpectedSpansAndCaptures(bool compiled, bool caseSensitive, bool allMatches)
    {
        foreach (SearchPatternFamily family in Enum.GetValues<SearchPatternFamily>())
        {
            var fixture = SearchPatternFixture.Create(family);
            var engine = new SelectStringEngine(new[] { fixture.Pattern }, caseSensitive: caseSensitive, allMatches: allMatches, compiledRegex: compiled);
            fixture.Validate(input =>
            {
                bool matched = engine.TryMatch(input, out var result);
                if (matched) { Assert.Equal(0, result.PatternIndex); }
                return matched ? result.Matches : null;
            });
        }
    }
}
