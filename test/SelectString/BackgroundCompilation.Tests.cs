// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerShell.Commands;
using PowerShell.Benchmarks.SelectString;
using Xunit;

namespace PSTests.SelectString;

public sealed class BackgroundCompilationTests
{
    [Fact]
    public void WorkThresholdWaitsForAnotherInputAndStopsTimingAfterDecision()
    {
        long clock = 0;
        var work = new MatchingWorkGate(2, timestamp: () => clock++);
        using var gate = new SemaphoreSlim(1, 1);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "(hit)" }),
            () => new SelectStringEngine(new[] { "(hit)" }, compiledRegex: true), gate: gate, workGate: work);
        try
        {
            matcher.TryMatch("hit", out var original);
            clock += 10000; // Reading/output delays between calls are not matching work.
            matcher.TryMatch("hit", out _);
            Assert.Equal(2, work.ObservedTicks);
            Assert.False(matcher.Admitted);
            matcher.TryMatch("hit", out _);
            Assert.True(matcher.Admitted);
            Assert.Equal(2, matcher.AdmissionAttemptedAtRecord);
            Assert.True(SpinWait.SpinUntil(() => matcher.PreparationFinished, TimeSpan.FromSeconds(10)));
            matcher.TryMatch("hit", out var promoted);
            Assert.Equal(original.Matches[0].Groups[1].Value, promoted.Matches[0].Groups[1].Value);
            Assert.Equal(2, work.Samples);
        }
        finally { matcher.Complete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkThresholdDoesNotAdmitOnFinalInputOrAfterStop(bool stop)
    {
        long clock = 0;
        var work = new MatchingWorkGate(1, timestamp: () => clock++);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new Exception("Must not compile"), workGate: work);
        matcher.TryMatch("hit", out _);
        Assert.True(work.Reached);
        if (stop)
        {
            matcher.RequestStop();
            matcher.TryMatch("hit", out _);
        }

        matcher.Complete();
        Assert.False(matcher.Admitted);
        Assert.Equal(1, work.Samples);
        Assert.Null(matcher.CompilationError);
    }

    [Fact]
    public void WorkAdmissionRejectsBusyGateOnceAndStopsSampling()
    {
        long clock = 0;
        var work = new MatchingWorkGate(1, timestamp: () => clock++);
        using var gate = new SemaphoreSlim(0, 1);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new Exception("Must not compile"), gate: gate, workGate: work);
        matcher.TryMatch("hit", out _);
        matcher.TryMatch("hit", out _);
        gate.Release();
        matcher.TryMatch("hit", out _);
        matcher.Complete();
        Assert.True(matcher.Rejected);
        Assert.False(matcher.Admitted);
        Assert.Equal(1, work.Samples);
        Assert.Equal(1, matcher.AdmissionAttemptedAtRecord);
    }

    [Fact]
    public void SampledWorkIsNotExtrapolated()
    {
        long clock = 0;
        var work = new MatchingWorkGate(3, sampleEvery: 4, timestamp: () => clock++);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new Exception("Must not compile"), workGate: work);
        for (int i = 0; i < 8; i++) { matcher.TryMatch("hit", out _); }
        matcher.Complete();
        Assert.Equal(2, work.Samples);
        Assert.Equal(2, work.ObservedTicks);
        Assert.False(work.Reached);
        Assert.False(matcher.Admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PromotionPreservesCultureAndExistingCaptures(bool workBased)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        using var gate = new SemaphoreSlim(1, 1);
        using var release = new ManualResetEventSlim();
        BackgroundCompilation matcher = null;
        long clock = 0;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var initial = new SelectStringEngine(new[] { "absent", "^(I)(.*)$" });
            matcher = new(initial, () =>
            {
                release.Wait();
                return new SelectStringEngine(new[] { "absent", "^(I)(.*)$" }, compiledRegex: true);
            }, startAfter: workBased ? 0 : 1, warmInput: "INPUT", gate: gate,
                workGate: workBased ? new MatchingWorkGate(1, timestamp: () => clock++) : null);
            Assert.True(matcher.TryMatch("\u0131nput", out var original));
            Assert.Equal(1, original.PatternIndex);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            // Queue under a different culture from engine construction.
            Assert.False(matcher.TryMatch("input", out _));
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => matcher.PreparationFinished, TimeSpan.FromSeconds(10)));
            Assert.True(matcher.TryMatch("\u0131nput", out var promoted));
            Assert.Equal(2, matcher.PromotedAtRecord);
            Assert.Equal(original.PatternIndex, promoted.PatternIndex);
            Assert.Equal("\u0131", original.Matches[0].Groups[1].Value);
            Assert.Equal(original.Matches[0].Value, promoted.Matches[0].Value);
            Assert.False(matcher.TryMatch("input", out _));
        }
        finally
        {
            release.Set();
            matcher?.Complete();
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void BusyWorkerRejectsWorkWithoutQueuing()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var initial = new SelectStringEngine(new[] { "hit" });
        var first = new BackgroundCompilation(initial, () =>
        {
            entered.Set();
            release.Wait();
            return new SelectStringEngine(new[] { "hit" }, compiledRegex: true);
        }, gate: gate);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var second = new BackgroundCompilation(initial, () => throw new Exception("Must not be queued"), gate: gate);
            Assert.True(second.Rejected);
            Assert.False(second.Admitted);
            Assert.True(second.TryMatch("hit", out _));
            second.Complete();
            Assert.Null(second.CompilationError);
        }
        finally
        {
            release.Set();
            first.Complete();
        }

        Assert.Equal(1, gate.CurrentCount);
        Assert.Null(first.CompilationError);
        Assert.Throws<ObjectDisposedException>(() => first.TryMatch("hit", out _));
    }

    [Fact]
    public void EarlyExitBeforeThresholdStartsNoWork()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new Exception("Must not start"), startAfter: 128, gate: gate);
        Assert.True(matcher.TryMatch("hit", out _));
        matcher.Complete();
        Assert.False(matcher.Admitted);
        Assert.False(matcher.CompilationStarted);
        Assert.Null(matcher.CompilationError);
    }

    [Fact]
    public void StopSuppressesDelayedAdmissionWithoutChangingMatching()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new Exception("Must not start"), startAfter: 1, gate: gate);
        try
        {
            Assert.True(matcher.TryMatch("hit", out _));
            matcher.RequestStop();
            Assert.True(matcher.TryMatch("hit", out _));
            Assert.False(matcher.Admitted);
        }
        finally { matcher.Complete(); }
    }

    [Fact]
    public async Task CompletionDrainsAnAlreadyRunningCompilation()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var draining = new ManualResetEventSlim();
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }), () =>
        {
            entered.Set();
            release.Wait();
            return new SelectStringEngine(new[] { "hit" }, compiledRegex: true);
        }, gate: gate);
        Task completion = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            completion = Task.Run(() =>
            {
                draining.Set();
                matcher.Complete();
            });
            Assert.True(draining.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            release.Set();
            if (completion != null) { await completion; }
            else { matcher.Complete(); }
        }

        Assert.True(matcher.CompilationStarted);
        Assert.Null(matcher.PromotedAtRecord);
        Assert.Null(matcher.CompilationError);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void DelayedPromotionStartsOnlyAfterThreshold()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => new SelectStringEngine(new[] { "hit" }, compiledRegex: true), startAfter: 2, gate: gate);
        try
        {
            Assert.True(matcher.TryMatch("hit", out _));
            Assert.True(matcher.TryMatch("hit", out _));
            Assert.False(matcher.Admitted);
            Assert.True(matcher.TryMatch("hit", out _));
            Assert.True(matcher.Admitted);
            Assert.True(SpinWait.SpinUntil(() => matcher.PreparationFinished, TimeSpan.FromSeconds(10)));
            Assert.True(matcher.TryMatch("hit", out _));
            Assert.InRange(matcher.PromotedAtRecord.Value, 2, 3);
        }
        finally { matcher.Complete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerFaultIsObservedAndInterpretationContinues(bool workBased)
    {
        using var gate = new SemaphoreSlim(1, 1);
        long clock = 0;
        var matcher = new BackgroundCompilation(new SelectStringEngine(new[] { "hit" }),
            () => throw new InvalidOperationException("Probe failure"), gate: gate,
            workGate: workBased ? new MatchingWorkGate(1, timestamp: () => clock++) : null);
        try
        {
            if (workBased)
            {
                matcher.TryMatch("hit", out _);
                matcher.TryMatch("hit", out _);
            }

            Assert.True(SpinWait.SpinUntil(() => matcher.PreparationFinished, TimeSpan.FromSeconds(10)));
            Assert.True(matcher.TryMatch("hit", out _));
        }
        finally { matcher.Complete(); }
        Assert.IsType<InvalidOperationException>(matcher.CompilationError);
        Assert.Null(matcher.PromotedAtRecord);
        Assert.Equal(1, gate.CurrentCount);
    }
}
