// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
public class PatternSetup
{
    [Params(false, true)]
    public bool Simple { get; set; }

    [Benchmark]
    public object Prepare() => new SelectStringEngine(new[] { "needle", "second" }, simpleMatch: Simple);
}

[MemoryDiagnoser]
public class EngineScanning
{
    private SelectStringEngine _engine;
    private string _directory;
    private string[] _paths;
    private string[] _records;
    private Encoding _encoding;
    private int _count;
    private Func<SelectStringEngine.Result, bool> _sink;

    [Params("String", "Records", "SmallFiles", "LargeFile", "LongLine", "Dense", "Early", "Late", "Context", "Utf16", "Utf32", "Legacy")]
    public string Scenario { get; set; }

    [Params(false, true)]
    public bool Simple { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _engine = new SelectStringEngine(new[] { "needle", "second" }, simpleMatch: Simple);
        _sink = result => { _count++; return true; };
        _directory = Path.Combine(Path.GetTempPath(), "SelectString-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _encoding = Scenario switch
        {
            "Utf16" => Encoding.Unicode,
            "Utf32" => Encoding.UTF32,
            "Legacy" => Encoding.GetEncoding(1252),
            _ => new UTF8Encoding(false),
        };
        _records = new string[Scenario == "String" ? 1 : 10000];
        for (int i = 0; i < _records.Length; i++)
        {
            _records[i] = i % 100 == 0 ? "needle café needle" : "ordinary input café without a match";
        }

        int files = Scenario == "SmallFiles" ? 256 : 1;
        _paths = new string[files];
        for (int f = 0; f < files; f++)
        {
            _paths[f] = Path.Combine(_directory, f + ".txt");
            using var writer = new StreamWriter(_paths[f], false, _encoding);
            int lines = Scenario == "SmallFiles" ? 16 : 100000;
            if (Scenario == "LongLine")
            {
                writer.Write(new string('x', 4 * 1024 * 1024));
                writer.Write("needle");
                continue;
            }

            for (int i = 0; i < lines; i++)
            {
                bool hit = Scenario switch
                {
                    "Early" => i == 0,
                    "Late" => i == lines - 1,
                    "Dense" => true,
                    _ => i % 100 == 0,
                };
                writer.WriteLine(hit ? "needle café needle" : "ordinary input café without a match");
            }
        }
    }

    [Benchmark]
    public int Scan()
    {
        _count = 0;
        if (Scenario == "String" || Scenario == "Records")
        {
            foreach (string record in _records)
            {
                if (_engine.TryMatch(record, out _))
                {
                    _count++;
                }
            }
        }
        else
        {
            int context = Scenario == "Context" ? 2 : 0;
            foreach (string path in _paths)
            {
                _engine.ScanFile(path, _encoding, context, context, Scenario == "Early" || Scenario == "Late", _sink);
            }
        }

        return _count;
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);
}
