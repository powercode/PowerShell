// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Microsoft.PowerShell.Commands;

namespace PowerShell.Benchmarks.SelectString;

/// <summary>
/// One engine per invocation, shared by all input records. Includes construction
/// in one method and isolates reuse in the other. No pipeline, file I/O or context.
/// </summary>
[MemoryDiagnoser]
public class PatternFamilyScanning
{
    private SearchPatternFixture _fixture;
    private SelectStringEngine _engine;
    private string[] _records;

    [ParamsAllValues]
    public SearchPatternFamily Family { get; set; }

    [ParamsAllValues]
    public SearchInputKind Input { get; set; }

    [Params(1, 10000)]
    public int Records { get; set; }

    [Params(false, true)]
    public bool Compiled { get; set; }

    [Params(true, false)]
    public bool CaseSensitive { get; set; }

    private SelectStringEngine CreateEngine() => new(new[] { _fixture.Pattern }, caseSensitive: CaseSensitive, compiledRegex: Compiled);

    [GlobalSetup]
    public void Setup()
    {
        _fixture = SearchPatternFixture.Create(Family);
        _fixture.Validate(Compiled, CaseSensitive, allMatches: false);
        _engine = CreateEngine();
        _records = new string[Records];
        for (int i = 0; i < Records; i++)
        {
            // Mixed cycles through success, absent prefix, short and long near misses.
            int index = Input == SearchInputKind.Mixed ? i % _fixture.Examples.Length : (int)Input;
            _records[i] = _fixture.Examples[index].Input;
        }

        Match(_engine);
    }

    [Benchmark]
    public int ConstructAndMatch() => Match(CreateEngine());

    [Benchmark]
    public int ReuseAndMatch() => Match(_engine);

    private int Match(SelectStringEngine engine)
    {
        int count = 0;
        foreach (string record in _records)
        {
            if (engine.TryMatch(record, out _)) { count++; }
        }

        return count;
    }
}
