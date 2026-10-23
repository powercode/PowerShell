// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Diagnostics.Tracing;

[EventSource(Name = "PowerShell-SelectString-Benchmark", Guid = "cf52a2f8-c51a-4c1e-9a75-62796b581a55")]
public sealed class SelectStringBenchmarkEvents : EventSource
{
    public static readonly SelectStringBenchmarkEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void WarmupBegin(string scenario) => WriteEvent(1, scenario);
    [Event(2, Level = EventLevel.Informational)]
    public void WarmupEnd(string scenario) => WriteEvent(2, scenario);
    [Event(3, Level = EventLevel.Informational)]
    public void IterationBegin(int iteration) => WriteEvent(3, iteration);
    [Event(4, Level = EventLevel.Informational)]
    public void IterationEnd(int iteration) => WriteEvent(4, iteration);
    [Event(5, Level = EventLevel.Informational)]
    public void CountingBegin(string scenario) => WriteEvent(5, scenario);
    [Event(6, Level = EventLevel.Informational)]
    public void CountingEnd(string scenario) => WriteEvent(6, scenario);
}
