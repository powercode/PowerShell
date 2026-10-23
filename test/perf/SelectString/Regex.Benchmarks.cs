// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

/// <summary>
/// Compares fresh construction plus first use with reuse of an already warmed engine.
/// Both variants use the production reader policy. These are engine measurements,
/// not cmdlet timings; generated logs and correctness validation are outside timing.
/// </summary>
[MemoryDiagnoser]
public class RegexScanning
{
    private string _directory;
    private string[] _paths;
    private string[] _patterns;
    private SelectStringEngine _engine;
    private Func<SelectStringEngine.Result, bool> _sink;
    private int _count;
    private readonly string _record = SelectStringLogFixture.Record(0);

    [Params(false, true)]
    public bool Compiled { get; set; }

    [Params(4, 1024, 16384, 307200)]
    public int SizeKiB { get; set; }

    // String and SmallFiles are fixed-size controls; use SizeKiB=4 when filtering them.
    [Params("LogSparse", "LogAllMatches", "Multiple", "Literal", "Early", "SmallFiles", "String")]
    public string Scenario { get; set; }

    private SelectStringEngine CreateEngine() => new(_patterns,
        allMatches: Scenario == "LogAllMatches", compiledRegex: Compiled);

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SelectString-regex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _paths = new string[Scenario == "SmallFiles" ? 256 : 1];
        long lines = 0;
        for (int i = 0; i < _paths.Length; i++)
        {
            _paths[i] = Path.Combine(_directory, i + ".log");
            lines = SelectStringLogFixture.Generate(_paths[i], (Scenario is "String" or "SmallFiles" ? 4 : SizeKiB) * 1024L);
        }

        string mode = Scenario == "LogAllMatches" ? Scenario : "LogSparse";
        string pattern = SelectStringLogFixture.Pattern(mode);
        _patterns = Scenario switch
        {
            "Multiple" => new[] { "never-present-[0-9]+", "also-absent-(a|b)+", pattern },
            "Literal" => new[] { "bucket=0000" },
            _ => new[] { pattern },
        };
        _sink = result => { _count++; return true; };
        _engine = CreateEngine();
        // Validate both the capture contents and every expected result before timing.
        if (Scenario != "Literal")
        {
            var validator = new SelectStringLogValidator(mode, lines);
            _engine.ScanFile(_paths[0], Encoding.UTF8, 0, 0, false, result =>
            {
                validator.Accept(result.LineNumber, result.Matches);
                return true;
            });
            validator.Complete();
        }

        int expected = Scan(_engine);
        if (expected == 0 || ConstructAndScan() != expected)
        {
            throw new InvalidDataException("Fresh and warmed engines disagree.");
        }
    }

    [Benchmark]
    public int ConstructAndScan() => Scan(CreateEngine());

    [Benchmark]
    public int ReuseAndScan() => Scan(_engine);

    private int Scan(SelectStringEngine engine)
    {
        _count = 0;
        if (Scenario == "String")
        {
            return engine.TryMatch(_record, out _) ? 1 : 0;
        }

        foreach (string path in _paths)
        {
            engine.ScanFile(path, Encoding.UTF8, 0, 0, Scenario == "Early", _sink);
        }

        return _count;
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);
}
