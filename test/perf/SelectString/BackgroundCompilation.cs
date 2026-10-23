// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

/// <summary>
/// Benchmark-only engine replacement. One foreground caller; at most one speculative
/// worker across instances. Busy admission falls back to interpretation without queuing.
/// Complete drains admitted work so benchmarks cannot hide its cost in later samples.
/// </summary>
internal sealed class BackgroundCompilation
{
    private static readonly SemaphoreSlim s_worker = new(1, 1);
    private readonly SemaphoreSlim _gate;
    private readonly Func<SelectStringEngine> _compile;
    private readonly CultureInfo _culture;
    private readonly int _startAfter;
    private readonly string _warmInput;
    private readonly MatchingWorkGate _workGate;
    private bool _admissionAttempted;
    private SelectStringEngine _active;
    private Task<SelectStringEngine> _pending;
    private int _closed;
    private int _records;
    private int _compilationStarted;
    private int _stopRequested;

    internal BackgroundCompilation(SelectStringEngine initial, Func<SelectStringEngine> compile,
        int startAfter = 0, string warmInput = null, SemaphoreSlim gate = null, MatchingWorkGate workGate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startAfter);
        if (workGate != null && startAfter != 0)
        {
            throw new ArgumentException("Choose work-based or record-based admission, not both.", nameof(startAfter));
        }

        _workGate = workGate;
        _active = initial;
        _compile = compile;
        _culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
        _startAfter = startAfter;
        _warmInput = warmInput;
        _gate = gate ?? s_worker;
        if (startAfter == 0 && workGate == null) { Start(); }
    }

    internal int? AdmissionAttemptedAtRecord { get; private set; }

    internal bool Admitted => _pending != null;

    internal static int ActiveWorkers => 1 - s_worker.CurrentCount;

    internal bool PreparationFinished => _pending?.IsCompleted ?? false;

    internal bool CompilationStarted => Volatile.Read(ref _compilationStarted) != 0;

    internal bool Rejected { get; private set; }

    internal int? PromotedAtRecord { get; private set; }

    internal Exception CompilationError { get; private set; }

    private void Start()
    {
        _admissionAttempted = true;
        AdmissionAttemptedAtRecord = _records;
        if (!_gate.Wait(0))
        {
            Rejected = true;
            return;
        }

        try
        {
            _pending = Task.Run(() =>
            {
                CultureInfo previous = CultureInfo.CurrentCulture;
                try
                {
                    if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _stopRequested) != 0) { return null; }
                    CultureInfo.CurrentCulture = _culture;
                    Volatile.Write(ref _compilationStarted, 1);
                    var replacement = _compile();
                    if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _stopRequested) != 0) { return null; }
                    // Explicit controlled-fixture probe only. Production code must not
                    // run arbitrary user patterns on invented input to force JIT.
                    if (_warmInput != null) { replacement.TryMatch(_warmInput, out _); }
                    return replacement;
                }
                finally
                {
                    CultureInfo.CurrentCulture = previous;
                    _gate.Release();
                }
            });
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    internal bool TryMatch(string line, out SelectStringEngine.MatchData result)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        bool stopped = Volatile.Read(ref _stopRequested) != 0;
        // Decide only when another input actually arrives. Crossing the work
        // threshold on the last record must not itself launch unused compilation.
        if (!stopped && !_admissionAttempted
            && (_workGate != null ? _workGate.Reached : _records == _startAfter && _startAfter != 0))
        {
            Start();
        }
        // Task completion publishes the entire engine. Never replace part of a
        // pattern array or change an engine while a matching call is using it.
        if (!stopped && PromotedAtRecord == null && _pending is { IsCompletedSuccessfully: true }
            && _pending.Result is { } replacement)
        {
            _active = replacement;
            PromotedAtRecord = _records;
        }

        bool sample = !stopped && !_admissionAttempted && _workGate != null && _workGate.ShouldSample(_records);
        _records++;
        if (!sample) { return _active.TryMatch(line, out result); }
        long start = _workGate.Timestamp();
        bool matched = _active.TryMatch(line, out result);
        _workGate.Observe(_workGate.Timestamp() - start);
        return matched;
    }

    internal void Complete()
    {
        // Suppress work that has not started, but compilation already in progress
        // cannot be interrupted. Join it and observe faults before measuring totals.
        Volatile.Write(ref _closed, 1);
        try { _pending?.GetAwaiter().GetResult(); }
        catch (Exception error) { CompilationError = error; }
    }

    internal void RequestStop() => Volatile.Write(ref _stopRequested, 1);
}

/// <summary>
/// Experimental admission from observed matching wall time, excluding reading/output.
/// Sampled time is not extrapolated to unsampled calls. Preemption/GC can inflate it,
/// and periodic sampling can miss expensive records; this is not a CPU-time budget.
/// </summary>
internal sealed class MatchingWorkGate
{
    private readonly long _thresholdTicks;
    private readonly int _sampleEvery;
    private readonly Func<long> _timestamp;

    internal MatchingWorkGate(long thresholdTicks, int sampleEvery = 1, Func<long> timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thresholdTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleEvery);
        _thresholdTicks = thresholdTicks;
        _sampleEvery = sampleEvery;
        _timestamp = timestamp;
    }

    internal long ObservedTicks { get; private set; }

    internal int Samples { get; private set; }

    internal bool Reached => ObservedTicks >= _thresholdTicks;

    internal bool ShouldSample(int record) => record % _sampleEvery == 0;

    internal long Timestamp() => _timestamp == null ? Stopwatch.GetTimestamp() : _timestamp();

    internal void Observe(long elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed);
        ObservedTicks = elapsed > long.MaxValue - ObservedTicks ? long.MaxValue : ObservedTicks + elapsed;
        Samples++;
    }
}
