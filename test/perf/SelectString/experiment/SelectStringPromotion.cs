// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Copied into the utility assembly ONLY in the isolated experimental worktree.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using PowerShell.Benchmarks.SelectString;

namespace Microsoft.PowerShell.Commands;

/// <summary>Experimental-build diagnostics, not a supported PowerShell API.</summary>
public static class SelectStringPromotionDiagnostics
{
    /// <summary>Test-only barrier; null during measurements.</summary>
    public static ManualResetEventSlim CompilationGate { get; set; }

    /// <summary>Signals entry into the test-only compilation barrier.</summary>
    public static ManualResetEventSlim CompilationEntered { get; } = new(false);

    /// <summary>Test-only fault injection; false during measurements.</summary>
    public static bool FailCompilation { get; set; }

    internal static long Completed;
    internal static long Admitted;
    internal static long Started;
    internal static long Rejected;
    internal static long Promoted;
    internal static long Faulted;
    internal static long DrainTicks;

    /// <summary>Returns cumulative counters; subtract snapshots outside timed calls.</summary>
    public static long[] Snapshot() => new[]
    {
        Interlocked.Read(ref Completed), Interlocked.Read(ref Admitted),
        Interlocked.Read(ref Started), Interlocked.Read(ref Rejected),
        Interlocked.Read(ref Promoted), Interlocked.Read(ref Faulted),
        Interlocked.Read(ref DrainTicks), (long)BackgroundCompilation.ActiveWorkers,
    };
}

internal sealed class SelectStringPromotion
{
    private readonly Func<SelectStringEngine> _compile;
    private readonly CultureInfo _culture;
    private readonly BackgroundCompilation _background;
    private SelectStringEngine _active;
    private int _records;
    private int _disposed;
    private int _stopped;

    internal static string Mode => Environment.GetEnvironmentVariable("SELECTSTRING_PROMOTION_MODE") ?? "background";

    internal static SelectStringPromotion Create(SelectStringEngine initial, string[] patterns,
        bool simple, bool caseSensitive, bool allMatches, bool notMatch, CompareInfo compareInfo, CompareOptions compareOptions)
    {
        if (simple || Mode is "interpreted" or "compiled") { return null; }
        // Snapshot all cmdlet arguments before dispatch. Workers never read cmdlet state.
        string[] snapshot = (string[])patterns.Clone();
        return new(initial, () =>
        {
            if (SelectStringPromotionDiagnostics.CompilationGate is { } gate)
            {
                SelectStringPromotionDiagnostics.CompilationEntered.Set();
                gate.Wait();
            }
            if (SelectStringPromotionDiagnostics.FailCompilation) { throw new InvalidOperationException("Injected compilation failure"); }
            return new SelectStringEngine(snapshot, caseSensitive: caseSensitive,
                allMatches: allMatches, notMatch: notMatch, compareInfo: compareInfo,
                compareOptions: compareOptions, compiledRegex: true);
        });
    }

    private SelectStringPromotion(SelectStringEngine initial, Func<SelectStringEngine> compile)
    {
        _active = initial;
        _compile = compile;
        _culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
        string mode = Mode;
        if (mode == "delayed-synchronous") { return; }
        if (mode is not ("background" or "background-warm" or "delayed-background-warm"))
        {
            throw new InvalidOperationException("Unknown experimental promotion mode: " + mode);
        }

        string warm = null;
        if (mode != "background")
        {
            warm = Environment.GetEnvironmentVariable("SELECTSTRING_PROMOTION_WARM_INPUT")
                ?? throw new InvalidOperationException("Warm modes require an explicit controlled fixture input");
        }

        _background = new(initial, compile, mode == "delayed-background-warm" ? 128 : 0, warm);
    }

    internal bool TryMatch(string line, out SelectStringEngine.MatchData result)
    {
        if (_background != null) { return _background.TryMatch(line, out result); }
        if (_records++ == 128 && Volatile.Read(ref _stopped) == 0)
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = _culture;
                _active = _compile();
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
        return _active.TryMatchCore(line, out result);
    }

    internal void RequestStop()
    {
        Volatile.Write(ref _stopped, 1);
        _background?.RequestStop();
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        long start = Stopwatch.GetTimestamp();
        _background?.Complete();
        Interlocked.Add(ref SelectStringPromotionDiagnostics.DrainTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref SelectStringPromotionDiagnostics.Completed);
        if (_background == null) { return; }
        if (_background.Admitted) { Interlocked.Increment(ref SelectStringPromotionDiagnostics.Admitted); }
        if (_background.CompilationStarted) { Interlocked.Increment(ref SelectStringPromotionDiagnostics.Started); }
        if (_background.Rejected) { Interlocked.Increment(ref SelectStringPromotionDiagnostics.Rejected); }
        if (_background.PromotedAtRecord != null) { Interlocked.Increment(ref SelectStringPromotionDiagnostics.Promoted); }
        if (_background.CompilationError != null) { Interlocked.Increment(ref SelectStringPromotionDiagnostics.Faulted); }
    }
}
