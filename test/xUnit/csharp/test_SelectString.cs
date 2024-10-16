// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.Parallel;

public class SelectStringTests
{
    private readonly string _filePath;

    public SelectStringTests()
    {
        SelectStringCommand.UseFileStreamFileLineReader = true;
        // Get a large test file locally for test purposes
         _filePath = Path.GetFullPath("war_and_peace.txt");
        if (!File.Exists(_filePath))
        {
            var text = new HttpClient().GetStringAsync("https://gutenberg.org/cache/epub/2600/pg2600.txt").Result;
            File.WriteAllText(_filePath, text, Encoding.UTF8);
        }
        
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
        foreach (var i in Enumerable.Range(1, 100)){
            var res = ps.Invoke<MatchInfo>();
        }
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
        string file = CreateTestFile(out string fileName);
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

    private static string CreateTestFile(out string fileName)
    {
        var nl = Environment.NewLine;
        var text = $"This is a text string, and another string{nl}This is the second line{nl}This is the third line{nl}This is the fourth line{nl}No matches";
        
        var file = Path.GetTempFileName();
        File.WriteAllText(file, text);
        fileName = Path.GetFileName(file);
        return file;
    }

    [Fact]
    public void ShouldProduceCorrectContextLines()
    {
        
        var file = CreateTestFile(out var fileName);
        using var ps = PowerShell.Create();
        string directoryName = Path.GetDirectoryName(file);
        ps.Commands
            .AddCommand("Push-Location")
            .AddParameter(nameof(PushLocationCommand.LiteralPath), directoryName)
            .AddStatement()
            .AddCommand("Select-String")
            .AddParameter(nameof(SelectStringCommand.LiteralPath), file)
            .AddParameter(nameof(SelectStringCommand.Pattern), "third")
            .AddParameter(nameof(SelectStringCommand.Context), 1)
            .AddParameter(nameof(SelectStringCommand.NoEmphasis), true);

        var matchInfo = ps.Invoke<MatchInfo>().First();
        
        var expected = $"""
                          {fileName}:2:This is the second line
                        > {fileName}:3:This is the third line
                          {fileName}:4:This is the fourth line
                        """;
        string actual = matchInfo.ToString(directoryName);
        Assert.Equal(expected, actual);
    }
}
