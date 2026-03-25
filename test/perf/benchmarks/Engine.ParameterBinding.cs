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

        private ScriptBlock namedBindingScript;
        private ScriptBlock positionalBindingScript;
        private ScriptBlock pipelineBindingScript;
        private ScriptBlock multipleParameterSetBindingScript;
        private ScriptBlock splattingBindingScript;
        private ScriptBlock validationAttributesBindingScript;
        private ScriptBlock pipelineByPropertyNameBindingScript;

        [GlobalSetup]
        public void GlobalSetup()
        {
            runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();
            Runspace.DefaultRunspace = runspace;

            string rootPath = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
            string escapedRootPath = rootPath.Replace("'", "''");

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
            splattingBindingScript = CreateScriptBlockWithRootPath(SplattingBindingScriptTemplate, escapedRootPath);

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
            SplattingBinding();
            ValidationAttributesBinding();
            PipelineByPropertyNameBinding();
        }

        private static ScriptBlock CreateScriptBlockWithRootPath(string scriptTemplate, string escapedRootPath)
        {
            return ScriptBlock.Create(string.Format(scriptTemplate, escapedRootPath));
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
        public Collection<PSObject> SplattingBinding() => splattingBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> ValidationAttributesBinding() => validationAttributesBindingScript.Invoke();

        [Benchmark]
        public Collection<PSObject> PipelineByPropertyNameBinding() => pipelineByPropertyNameBindingScript.Invoke();

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            runspace.Dispose();
            Runspace.DefaultRunspace = null;
        }
    }
}
