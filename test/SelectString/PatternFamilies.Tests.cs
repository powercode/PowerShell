// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using PowerShell.Benchmarks.SelectString;
using Xunit;

namespace PSTests.SelectString;

public sealed class PatternFamiliesTests
{
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
            SearchPatternFixture.Create(family).Validate(compiled, caseSensitive, allMatches);
        }
    }
}
