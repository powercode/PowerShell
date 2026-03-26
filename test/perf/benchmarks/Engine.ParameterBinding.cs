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
