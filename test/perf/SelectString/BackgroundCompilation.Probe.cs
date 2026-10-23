// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

/// <summary>
/// Exploratory probe, not a BenchmarkDotNet job. Measures the foreground separately
/// from draining speculative work, and retains per-call CPU/allocation observations.
/// Usage: --promotion-probe output-directory affinity-mask (0 keeps OS affinity).
/// </summary>
internal static class BackgroundCompilationProbe
{
    private enum Mode { Interpreted, Compiled, DelayedSynchronous, BackgroundConstructor, BackgroundWarm, BackgroundDelayedWarm, WrapperOnly, TimingEveryCall, TimingSampled, WorkEveryCall, WorkSampled }

    private sealed record Sample(double ForegroundMs, double TotalMs, double CpuMs, long AllocatedBytes,
        double? FirstResultMs, double? PublicationObservedMs, int Matches, bool Admitted, bool CompilationStarted, bool Rejected, int? PromotedAtRecord, int? AdmissionAttemptedAtRecord, double ObservedMatchingMs, int TimedMatchingCalls);

    internal static void Run(string[] args)
    {
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "admission"))
        {
            throw new ArgumentException("Usage: --promotion-probe output-directory affinity-mask [admission]");
        }

        bool admission = args.Length == 4;
        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        long affinity = long.Parse(args[2], CultureInfo.InvariantCulture);
        using var process = Process.GetCurrentProcess();
        if (affinity != 0)
        {
            if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Probe affinity currently requires Windows"); }
            process.ProcessorAffinity = (nint)affinity;
        }

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        const int warmups = 3;
        const int iterations = 9;
        SearchPatternFamily[] families = admission
            ? new[] { SearchPatternFamily.Literal, SearchPatternFamily.SqlDuration }
            : new[] { SearchPatternFamily.Literal, SearchPatternFamily.DotStar, SearchPatternFamily.DotPlus, SearchPatternFamily.SqlDuration };
        foreach (SearchPatternFamily family in families)
        {
            var fixture = SearchPatternFixture.Create(family);
            // Validate interpreted/compiled spans and groups independently of the probe.
            foreach (bool compiled in new[] { false, true })
            {
                var engine = new SelectStringEngine(new[] { fixture.Pattern }, caseSensitive: true, compiledRegex: compiled);
                fixture.Validate(input => engine.TryMatch(input, out var result) ? result.Matches : null);
            }

            foreach (SearchInputKind input in admission ? new[] { SearchInputKind.Match, SearchInputKind.LongNearMiss } : new[] { SearchInputKind.Match, SearchInputKind.Absent, SearchInputKind.LongNearMiss })
            {
                foreach (int records in new[] { 1, admission ? 4096 : 10000 })
                {
                    var modes = Enum.GetValues<Mode>().Where(mode => admission || mode <= Mode.BackgroundDelayedWarm).ToArray();
                    var samples = modes.ToDictionary(mode => mode, _ => new List<Sample>());
                    for (int round = -warmups; round < iterations; round++)
                    {
                        // Rotate order, draining every operation before the next starts.
                        for (int offset = 0; offset < modes.Length; offset++)
                        {
                            Mode mode = modes[(round + warmups + offset) % modes.Length];
                            Sample sample = Measure(fixture, input, records, mode, process);
                            if (round >= 0) { samples[mode].Add(sample); }
                        }
                    }

                    var rows = modes.Select(mode => new
                    {
                        Family = family.ToString(), Input = input.ToString(), Records = records,
                        Mode = mode.ToString(), fixture.Pattern, fixture.Source,
                        AdmissionProfile = admission,
                        WorkThresholdMs = mode is Mode.WorkEveryCall or Mode.WorkSampled ? 0.5 : (double?)null,
                        SampleEvery = mode is Mode.TimingSampled or Mode.WorkSampled ? 16 : 1,
                        Warmups = warmups, Iterations = iterations, Affinity = affinity,
                        ProcessorCount = Environment.ProcessorCount,
                        Runtime = RuntimeInformation.FrameworkDescription,
                        Culture = CultureInfo.CurrentCulture.Name, CaseSensitive = true,
                        MedianForegroundMs = Median(samples[mode].Select(sample => sample.ForegroundMs)),
                        MedianTotalMs = Median(samples[mode].Select(sample => sample.TotalMs)),
                        // Individual process CPU observations may be below the clock's resolution.
                        MeanCpuMs = samples[mode].Average(sample => sample.CpuMs),
                        MeanAllocatedBytes = samples[mode].Average(sample => sample.AllocatedBytes),
                        PromotedCalls = samples[mode].Count(sample => sample.PromotedAtRecord != null),
                        UnusedCompilations = samples[mode].Count(sample => sample.CompilationStarted && sample.PromotedAtRecord == null),
                        Samples = samples[mode],
                    });
                    string name = $"{family}-{input}-{records}";
                    File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine(name);
                }
            }
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static Sample Measure(SearchPatternFixture fixture, SearchInputKind input, int records, Mode mode, Process process)
    {
        var example = fixture.Examples[(int)input];
        Func<SelectStringEngine> compile = () => new(new[] { fixture.Pattern }, caseSensitive: true, compiledRegex: true);
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        BackgroundCompilation background = null;
        MatchingWorkGate work = null;
        int count = 0;
        double? first = null;
        double? publication = null;
        var timer = Stopwatch.StartNew();
        double foreground;
        try
        {
            work = mode switch
            {
                Mode.TimingEveryCall => new(long.MaxValue),
                Mode.TimingSampled => new(long.MaxValue, sampleEvery: 16),
                Mode.WorkEveryCall => new(Math.Max(1, Stopwatch.Frequency / 2000)),
                Mode.WorkSampled => new(Math.Max(1, Stopwatch.Frequency / 2000), sampleEvery: 16),
                _ => null,
            };
            var engine = mode == Mode.Compiled ? compile() : new SelectStringEngine(new[] { fixture.Pattern }, caseSensitive: true);
            if (mode is Mode.BackgroundConstructor or Mode.BackgroundWarm or Mode.BackgroundDelayedWarm or Mode.WrapperOnly || work != null)
            {
                background = new(engine, compile,
                    startAfter: mode == Mode.WrapperOnly ? int.MaxValue : mode == Mode.BackgroundDelayedWarm ? 128 : 0,
                    warmInput: mode == Mode.BackgroundConstructor ? null : example.Input,
                    workGate: work);
            }

            for (int i = 0; i < records; i++)
            {
                if (mode == Mode.DelayedSynchronous && i == 128) { engine = compile(); }
                bool matched = background == null ? engine.TryMatch(example.Input, out _) : background.TryMatch(example.Input, out _);
                if (publication == null && background?.PromotedAtRecord != null) { publication = timer.Elapsed.TotalMilliseconds; }
                if (matched)
                {
                    first ??= timer.Elapsed.TotalMilliseconds;
                    count++;
                }
            }

            foreground = timer.Elapsed.TotalMilliseconds;
        }
        finally { background?.Complete(); }
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        if (background?.CompilationError != null) { throw new InvalidOperationException("Speculative compilation failed", background.CompilationError); }
        if (count != (example.Value == null ? 0 : records)) { throw new InvalidDataException("Probe result count mismatch"); }
        return new(foreground, timer.Elapsed.TotalMilliseconds, cpu, allocated, first, publication, count,
            background?.Admitted ?? false, background?.CompilationStarted ?? false, background?.Rejected ?? false, background?.PromotedAtRecord, background?.AdmissionAttemptedAtRecord,
            (work?.ObservedTicks ?? 0) * 1000.0 / Stopwatch.Frequency, work?.Samples ?? 0);
    }
}
