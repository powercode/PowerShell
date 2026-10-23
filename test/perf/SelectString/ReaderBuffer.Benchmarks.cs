// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

/// <summary>
/// Isolates reader buffer size while retaining the engine's complete file scan.
/// SELECTSTRING_LOG_FIXTURE must name a generated log fixture directory.
/// Setup validates the file and captures outside timing; scans discard results.
/// </summary>
[MemoryDiagnoser]
public class ReaderBufferScanning
{
    private SelectStringEngine _engine;
    private string[] _paths;
    private string _smallDirectory;
    private Func<SelectStringEngine.Result, bool> _sink;
    private int _count;

    [Params("LogSparse", "LogAllMatches", "SmallFiles", "Early")]
    public string Scenario { get; set; }

    [Params(1024, 16384, 65536)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sink = result => { _count++; return true; };
        if (Scenario is "SmallFiles" or "Early")
        {
            _smallDirectory = Path.Combine(Path.GetTempPath(), "SelectString-buffers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_smallDirectory);
            _paths = new string[Scenario == "SmallFiles" ? 256 : 1];
            for (int i = 0; i < _paths.Length; i++)
            {
                _paths[i] = Path.Combine(_smallDirectory, i + ".log");
                File.WriteAllText(_paths[i], "needle café\n" + new string('x', Scenario == "Early" ? 100000 : 256));
            }

            _engine = new SelectStringEngine(new[] { "needle" });
            if (Scan() != _paths.Length) { throw new InvalidDataException("Incorrect small-file match count"); }
            return;
        }

        string directory = Environment.GetEnvironmentVariable("SELECTSTRING_LOG_FIXTURE")
            ?? throw new InvalidOperationException("Set SELECTSTRING_LOG_FIXTURE to an existing log fixture directory.");
        _paths = new[] { Path.Combine(directory, "application.log") };
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        using (var stream = File.OpenRead(_paths[0]))
        {
            if (manifest.RootElement.GetProperty("Version").GetInt32() != 1 ||
                Convert.ToHexString(SHA256.HashData(stream)) != manifest.RootElement.GetProperty("SHA256").GetString())
            {
                throw new InvalidDataException("Log fixture version/hash mismatch");
            }
        }

        _engine = new SelectStringEngine(new[] { SelectStringLogFixture.Pattern(Scenario) }, allMatches: Scenario == "LogAllMatches");
        var validator = new SelectStringLogValidator(Scenario, manifest.RootElement.GetProperty("Lines").GetInt64());
        _engine.ScanFile(_paths[0], Encoding.UTF8, 0, 0, false, result =>
        {
            validator.Accept(result.LineNumber, result.Matches);
            return true;
        }, readerBufferSize: BufferSize);
        validator.Complete();
    }

    [Benchmark]
    public int Scan()
    {
        _count = 0;
        foreach (string path in _paths)
        {
            _engine.ScanFile(path, Encoding.UTF8, 0, 0, Scenario == "Early", _sink, readerBufferSize: BufferSize);
        }

        return _count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_smallDirectory != null) { Directory.Delete(_smallDirectory, recursive: true); }
    }
}

/// <summary>
/// Measures the file-size crossover on the same synthetic log distribution.
/// Each operation opens and scans one file. Construction and validation are untimed.
/// </summary>
[MemoryDiagnoser]
public class ReaderBufferFileSizes
{
    private SelectStringEngine _engine;
    private string _directory;
    private string _path;
    private int _count;
    private Func<SelectStringEngine.Result, bool> _sink;

    [Params(4, 64, 1024, 16384)]
    public int SizeKiB { get; set; }

    [Params("LogSparse", "LogAllMatches")]
    public string Scenario { get; set; }

    [Params(1024, 16384, 65536)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SelectString-sizes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "application.log");
        long lines = SelectStringLogFixture.Generate(_path, SizeKiB * 1024L);
        _engine = new SelectStringEngine(new[] { SelectStringLogFixture.Pattern(Scenario) }, allMatches: Scenario == "LogAllMatches");
        _sink = result => { _count++; return true; };
        var validator = new SelectStringLogValidator(Scenario, lines);
        _engine.ScanFile(_path, Encoding.UTF8, 0, 0, false, result =>
        {
            validator.Accept(result.LineNumber, result.Matches);
            return true;
        }, readerBufferSize: BufferSize);
        validator.Complete();
    }

    [Benchmark]
    public int Scan()
    {
        _count = 0;
        _engine.ScanFile(_path, Encoding.UTF8, 0, 0, false, _sink, readerBufferSize: BufferSize);
        return _count;
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);
}
