// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using BenchmarkDotNet.Attributes;
using MicroBenchmarks;

namespace Engine
{
    [BenchmarkCategory(Categories.Engine, Categories.Internal)]
    public class ParameterBinding
    {
        private const string NamedBindingScriptTemplate = @"
$benchmarkPath = @'
{0}
'@
Get-ChildItem -Path $benchmarkPath -Recurse -Depth 2 -ErrorAction SilentlyContinue
";

        private const string PositionalBindingScriptTemplate = @"
$benchmarkPath = @'
{0}
'@
Get-ChildItem $benchmarkPath -ErrorAction SilentlyContinue
";

        private const string SplattingBindingScriptTemplate = @"
$benchmarkPath = @'
{0}
'@
$parameters = @{{
    Path = $benchmarkPath
    Recurse = $true
    ErrorAction = 'SilentlyContinue'
}}
Get-ChildItem @parameters
";

        private Runspace runspace;
        private string benchmarkRootPath;

        private ScriptBlock namedBindingScript;
        private ScriptBlock positionalBindingScript;
        private ScriptBlock pipelineBindingScript;
        private ScriptBlock multipleParameterSetBindingScript;
        private ScriptBlock parameterSetByIdBindingScript;
        private ScriptBlock splattingBindingScript;
        private ScriptBlock splattingAdvancedFunctionBindingScript;
        private ScriptBlock directAdvancedFunctionBindingScript;
        private ScriptBlock validationAttributesBindingScript;
        private ScriptBlock pipelineByPropertyNameBindingScript;
        private ScriptBlock delayBindScriptBlockScript;
        private ScriptBlock dynamicParameterScript;
        private ScriptBlock largeParameterCountScript;
        private ScriptBlock vraScript;
        private ScriptBlock defaultParameterValuesScript;
        private ScriptBlock pipelineTypeCoercionScript;
        private ScriptBlock highVolumePipelineBindingScript;
        private ScriptBlock highVolumePipelineByPropertyNameBindingScript;

        [GlobalSetup]
        public void GlobalSetup()
        {
            runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();
            Runspace.DefaultRunspace = runspace;

            benchmarkRootPath = CreateBenchmarkDirectory();
            string escapedRootPath = benchmarkRootPath.Replace("'", "''");

            namedBindingScript = CreateScriptBlockWithRootPath(NamedBindingScriptTemplate, escapedRootPath);
            positionalBindingScript = CreateScriptBlockWithRootPath(PositionalBindingScriptTemplate, escapedRootPath);
            pipelineBindingScript = ScriptBlock.Create("1..100 | ForEach-Object { $_ }");

            multipleParameterSetBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingMultiSet {
    [CmdletBinding(DefaultParameterSetName='ByName')]
    param(
        [Parameter(Mandatory, ParameterSetName='ByName')]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName='ById')]
        [int]$Id,

        [Parameter(ParameterSetName='ByName')]
        [switch]$AsString,

        [Parameter(ParameterSetName='ById')]
        [switch]$AsHex
    )

    if ($PSCmdlet.ParameterSetName -eq 'ByName') {
        return $Name
    }

    if ($AsHex) {
        return ('0x{0:X}' -f $Id)
    }

    return $Id
}

Test-ParameterBindingMultiSet -Name 'benchmark' -AsString
");

            parameterSetByIdBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingMultiSet {
    [CmdletBinding(DefaultParameterSetName='ByName')]
    param(
        [Parameter(Mandatory, ParameterSetName='ByName')]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName='ById')]
        [int]$Id,

        [Parameter(ParameterSetName='ByName')]
        [switch]$AsString,

        [Parameter(ParameterSetName='ById')]
        [switch]$AsHex
    )

    if ($PSCmdlet.ParameterSetName -eq 'ByName') {
        return $Name
    }

    if ($AsHex) {
        return ('0x{0:X}' -f $Id)
    }

    return $Id
}

Test-ParameterBindingMultiSet -Id 42 -AsHex
");
            splattingBindingScript = CreateScriptBlockWithRootPath(SplattingBindingScriptTemplate, escapedRootPath);

            splattingAdvancedFunctionBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingSplattingTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$Count,

        [switch]$Enabled
    )

    if ($Enabled) {
        return ('{0}{1}' -f $Name, $Count)
    }

    return $Count
}

$parameters = @{
    Name = 'item'
    Count = 12
    Enabled = $true
}

Test-ParameterBindingSplattingTarget @parameters
");

            directAdvancedFunctionBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingSplattingTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$Count,

        [switch]$Enabled
    )

    if ($Enabled) {
        return ('{0}{1}' -f $Name, $Count)
    }

    return $Count
}

Test-ParameterBindingSplattingTarget -Name 'item' -Count 12 -Enabled
");

            validationAttributesBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingValidation {
    [CmdletBinding()]
    param(
        [ValidateSet('Alpha', 'Beta', 'Gamma')]
        [string]$Name,

        [ValidateRange(1, 100)]
        [int]$Count,

        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    return ""$Name$Count$Path""
}

Test-ParameterBindingValidation -Name 'Alpha' -Count 10 -Path 'item'
");

            pipelineByPropertyNameBindingScript = ScriptBlock.Create(@"
function Test-ParameterBindingByPropertyName {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipelineByPropertyName)]
        [string]$Name,

        [Parameter(ValueFromPipelineByPropertyName)]
        [int]$Count
    )

    process {
        ""$Name$Count""
    }
}

[pscustomobject]@{ Name = 'item'; Count = 5 } | Test-ParameterBindingByPropertyName
");

            var delayBindTemplate = @"
$benchmarkPath = @'
__ROOT_PATH__
'@
function Test-DelayBind {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [object]$InputObject,

        [string]$Path
    )

    process {
        $Path
    }
}

Get-ChildItem -Path $benchmarkPath -File -Recurse |
    Test-DelayBind -Path { $_.FullName } |
    Out-Null
";
            delayBindScriptBlockScript = ScriptBlock.Create(delayBindTemplate.Replace("__ROOT_PATH__", escapedRootPath));

            var dynamicParameterTemplate = @"
$benchmarkPath = @'
__ROOT_PATH__
'@
$csvPath = Join-Path $benchmarkPath 'dynamic-parameter-data.csv'
if (-not (Test-Path -Path $csvPath)) {
    'a,b,c,d,e' | Set-Content -Path $csvPath -NoNewline
}

Get-Content -Path $csvPath -Delimiter ',' | Out-Null
";
            dynamicParameterScript = ScriptBlock.Create(dynamicParameterTemplate.Replace("__ROOT_PATH__", escapedRootPath));

            largeParameterCountScript = ScriptBlock.Create(@"
function Test-LargeParameterCount {
    [CmdletBinding()]
    param(
        [string]$P01, [string]$P02, [string]$P03, [string]$P04, [string]$P05,
        [string]$P06, [string]$P07, [string]$P08, [string]$P09, [string]$P10,
        [string]$P11, [string]$P12, [string]$P13, [string]$P14, [string]$P15,
        [string]$P16, [string]$P17, [string]$P18, [string]$P19, [string]$P20,
        [string]$P21, [string]$P22, [string]$P23, [string]$P24, [string]$P25
    )

    '{0}{1}' -f $P01, $P25
}

Test-LargeParameterCount `
    -P01 'a' -P02 'b' -P03 'c' -P04 'd' -P05 'e' `
    -P06 'f' -P07 'g' -P08 'h' -P09 'i' -P10 'j' `
    -P11 'k' -P12 'l' -P13 'm' -P14 'n' -P15 'o' `
    -P16 'p' -P17 'q' -P18 'r' -P19 's' -P20 't' `
    -P21 'u' -P22 'v' -P23 'w' -P24 'x' -P25 'y' |
    Out-Null
");

            vraScript = ScriptBlock.Create(@"
function Test-VRA {
    [CmdletBinding()]
    param(
        [Parameter(Position=0)]
        [string]$First,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Rest
    )

    $Rest.Count
}

Test-VRA 'start' '1' '2' '3' '4' '5' '6' '7' '8' '9' '10' '11' '12' '13' '14' '15' '16' '17' '18' '19' '20' | Out-Null
");

            defaultParameterValuesScript = ScriptBlock.Create(@"
$oldDefault = $PSDefaultParameterValues
$PSDefaultParameterValues = @{
    'Test-DefaultBenchmark:Name' = 'benchmark'
    'Test-DefaultBenchmark:Count' = 7
    'Test-DefaultBenchmark:Path' = 'root'
}

function Test-DefaultBenchmark {
    [CmdletBinding()]
    param(
        [string]$Name,
        [int]$Count,
        [string]$Path
    )

    '{0}{1}{2}' -f $Name, $Count, $Path
}

try {
    Test-DefaultBenchmark | Out-Null
}
finally {
    $PSDefaultParameterValues = $oldDefault
}
");

            // Pre-create the 100 000-element input arrays once so that each benchmark iteration
            // measures only the pipeline binding loop, not object construction.
            ScriptBlock.Create("$global:_bench100kInts = [int[]](1..100000)").Invoke();
            ScriptBlock.Create("$global:_bench100kObjs = [object[]](1..100000 | ForEach-Object { [pscustomobject]@{ Name = 'item'; Count = $_ } })").Invoke();

            pipelineTypeCoercionScript = ScriptBlock.Create(@"
function Test-PipelineCoercion {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [int]$Value
    )

    process {
        $Value
    }
}

1..100 | ForEach-Object { $_.ToString() } | Test-PipelineCoercion | Out-Null
");

            highVolumePipelineBindingScript = ScriptBlock.Create(@"
function Test-HighVolumePipeline {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [int]$Value
    )
    process { }
}
$global:_bench100kInts | Test-HighVolumePipeline | Out-Null
");

            highVolumePipelineByPropertyNameBindingScript = ScriptBlock.Create(@"
function Test-HighVolumePipelineByPropertyName {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipelineByPropertyName)]
        [string]$Name,
        [Parameter(ValueFromPipelineByPropertyName)]
        [int]$Count
    )
    process { }
}
$global:_bench100kObjs | Test-HighVolumePipelineByPropertyName | Out-Null
");

            // Warm all scripts once to avoid startup and first-JIT noise in benchmark iterations.
            NamedParameterBinding();
            PositionalParameterBinding();
            PipelineParameterBinding();
            AdvancedFunctionMultipleParameterSets();
            AdvancedFunctionMultipleParameterSetsById();
            SplattingBinding();
            AdvancedFunctionSplattingBinding();
            AdvancedFunctionDirectBinding();
            ValidationAttributesBinding();
            PipelineByPropertyNameBinding();
            DelayBindScriptBlockBinding();
            DynamicParameterBinding();
            LargeParameterCountBinding();
            ValueFromRemainingArgumentsBinding();
            DefaultParameterValuesBinding();
            PipelineWithTypeCoercionBinding();
            HighVolumePipelineBinding();
            HighVolumePipelineByPropertyNameBinding();
        }

        private static ScriptBlock CreateScriptBlockWithRootPath(string scriptTemplate, string escapedRootPath)
        {
            return ScriptBlock.Create(string.Format(scriptTemplate, escapedRootPath));
        }

        private static string CreateBenchmarkDirectory()
        {
            string benchmarkRoot = Path.Combine(Path.GetTempPath(), "pwsh-parameterbinding-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(benchmarkRoot);

            for (int i = 0; i < 5; i++)
            {
                string level1 = Path.Combine(benchmarkRoot, $"dir{i}");
                Directory.CreateDirectory(level1);

                for (int j = 0; j < 5; j++)
                {
                    string level2 = Path.Combine(level1, $"sub{j}");
                    Directory.CreateDirectory(level2);

                    for (int k = 0; k < 4; k++)
                    {
                        File.WriteAllText(Path.Combine(level2, $"file{k}.txt"), "benchmark");
                    }
                }
            }

            return benchmarkRoot;
        }

        [Benchmark]
        public Collection<PSObject> NamedParameterBinding() => namedBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> PositionalParameterBinding() => positionalBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> PipelineParameterBinding() => pipelineBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> AdvancedFunctionMultipleParameterSets() => multipleParameterSetBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> AdvancedFunctionMultipleParameterSetsById() => parameterSetByIdBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> SplattingBinding() => splattingBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> AdvancedFunctionSplattingBinding() => splattingAdvancedFunctionBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> AdvancedFunctionDirectBinding() => directAdvancedFunctionBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> ValidationAttributesBinding() => validationAttributesBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> PipelineByPropertyNameBinding() => pipelineByPropertyNameBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> DelayBindScriptBlockBinding() => delayBindScriptBlockScript.Invoke();

        [Benchmark]
        public Collection<PSObject> DynamicParameterBinding() => dynamicParameterScript.Invoke();

        [Benchmark]
        public Collection<PSObject> LargeParameterCountBinding() => largeParameterCountScript.Invoke();

        [Benchmark]
        public Collection<PSObject> ValueFromRemainingArgumentsBinding() => vraScript.Invoke();

        [Benchmark]
        public Collection<PSObject> DefaultParameterValuesBinding() => defaultParameterValuesScript.Invoke();

        [Benchmark]
        public Collection<PSObject> PipelineWithTypeCoercionBinding() => pipelineTypeCoercionScript.Invoke();

        /// <summary>
        /// Pipes 100 000 pre-created integers to a function with a single
        /// <c>[Parameter(ValueFromPipeline)]</c> parameter.
        /// Measures per-element pipeline binding overhead at high volume.
        /// </summary>
        [Benchmark]
        public Collection<PSObject> HighVolumePipelineBinding() => highVolumePipelineBindingScript.Invoke();

        /// <summary>
        /// Pipes 100 000 pre-created PSObjects to a function with two
        /// <c>[Parameter(ValueFromPipelineByPropertyName)]</c> parameters.
        /// Measures property-name matching and binding overhead at high volume.
        /// </summary>
        [Benchmark]
        public Collection<PSObject> HighVolumePipelineByPropertyNameBinding() => highVolumePipelineByPropertyNameBindingScript.Invoke();

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            runspace.Dispose();
            Runspace.DefaultRunspace = null;

            if (!string.IsNullOrWhiteSpace(benchmarkRootPath) && Directory.Exists(benchmarkRootPath))
            {
                Directory.Delete(benchmarkRootPath, recursive: true);
            }
        }
    }
}
