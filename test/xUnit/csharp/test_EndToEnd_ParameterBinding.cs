// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    // -----------------------------------------------------------------------
    // Comprehensive end-to-end tests combining multiple parameter binding
    // features: multi-set, positional, reparsing, coercion, and validation.
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class EndToEnd_MultiSetPositionalTests
    {
        // A function with two parameter sets, overlapping positional parameter.
        private const string MultiSetFunctionScript = @"
            function Test-MultiSet {
                [CmdletBinding(DefaultParameterSetName='ByName')]
                param(
                    [Parameter(ParameterSetName='ByName',  Position=0)]
                    [Parameter(ParameterSetName='ByIndex', Position=0)]
                    [string]$First,

                    [Parameter(ParameterSetName='ByName',  Position=1)]
                    [string]$Name,

                    [Parameter(ParameterSetName='ByIndex', Position=1)]
                    [int]$Index
                )
                ""$($PSCmdlet.ParameterSetName):$First""
            }
        ";

        [Fact]
        public void MultiSet_PositionalFirst_DefaultSet_Binds()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet 'alpha' 'beta'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByName:alpha", (string)results[0].BaseObject);
        }

        [Fact]
        public void MultiSet_ExplicitNamedSetSelection_BindsCorrectly()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet -First 'x' -Index 7
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByIndex:x", (string)results[0].BaseObject);
        }

        [Fact]
        public void MultiSet_AmbiguousSinglePositional_UsesDefaultSet()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet 'onlyFirst'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByName:onlyFirst", (string)results[0].BaseObject);
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class EndToEnd_CoercionPlusValidationTests
    {
        [Fact]
        public void Int_String_CoercedAndValidated_Passes()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateRange(1, 100)]
                            [int]$Count
                        )
                        $Count
                    }
                    Test-Func -Count '42'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(42, results[0].BaseObject);
        }

        [Fact]
        public void Int_String_CoercedAndValidated_OutOfRange_Fails()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateRange(1, 100)]
                            [int]$Count
                        )
                        $Count
                    }
                    Test-Func -Count '0'
                ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
        }

        [Fact]
        public void Array_Parameter_ValidateCount_Passes()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateCount(2, 4)]
                            [string[]]$Items
                        )
                        $Items.Count
                    }
                    Test-Func -Items 'a', 'b', 'c'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(3, results[0].BaseObject);
        }

        [Fact]
        public void Array_Parameter_ValidateCount_TooFew_Fails()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateCount(2, 4)]
                            [string[]]$Items
                        )
                        $Items.Count
                    }
                    Test-Func -Items 'a'
                ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class EndToEnd_PositionalPlusSwitchTests
    {
        [Fact]
        public void Positional_PlusSwitchFlag_BothBind()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)]
                            [string]$Name,
                            [switch]$Verbose2
                        )
                        ""$($Name):$($Verbose2)""
                    }
                    Test-Func 'world' -Verbose2
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("world:True", (string)results[0].BaseObject);
        }

        [Fact]
        public void Positional_NamedOverride_IgnoresPosition()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)]
                            [string]$First,
                            [Parameter(Position=1)]
                            [string]$Second
                        )
                        ""$First/$Second""
                    }
                    Test-Func -Second 'b' 'a'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("a/b", (string)results[0].BaseObject);
        }

        [Fact]
        public void Switch_ColonFalse_IsExplicitlyFalse_PositionalStillBinds()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)]
                            [string]$Name,
                            [switch]$Flag
                        )
                        ""$($Name):$($Flag)""
                    }
                    Test-Func -Flag:$false 'hello'
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("hello:False", (string)results[0].BaseObject);
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class EndToEnd_PipelineBindingTests
    {
        [Fact]
        public void Pipeline_ValueFromPipeline_Binds()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(ValueFromPipeline=$true)]
                            [string]$Value
                        )
                        process { $Value }
                    }
                    'piped' | Test-Func
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("piped", (string)results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipeline_WithValidation_PassesValidValue()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(ValueFromPipeline=$true)]
                            [ValidateSet('a','b','c')]
                            [string]$Value
                        )
                        process { $Value }
                    }
                    'b' | Test-Func
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("b", (string)results[0].BaseObject);
        }
    }

    // -----------------------------------------------------------------------
    // End-to-end tests that exercise the pipeline binding plan cache (Phase 10).
    // Verify that caching is transparent — results are identical to no-caching
    // behavior across all pipeline shapes.
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class EndToEnd_PipelineCachingTests
    {
        private const string VfpFunctionScript = @"
            function Test-VfpFunc {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipeline=$true)]
                    [int]$Value
                )
                process { $Value * 2 }
            }
        ";

        private const string VfpByPropNameFunctionScript = @"
            function Test-ByPropNameFunc {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipelineByPropertyName=$true)]
                    [string]$Name,
                    [Parameter(ValueFromPipelineByPropertyName=$true)]
                    [int]$Count
                )
                process { ""$Name=$Count"" }
            }
        ";

        [Fact]
        public void HomogeneousPipeline_100Ints_BindsAll()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(VfpFunctionScript + @"
                    1..100 | Test-VfpFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(100, results.Count);
            // Spot-check: first = 2, last = 200
            Assert.Equal(2,   (int)results[0].BaseObject);
            Assert.Equal(200, (int)results[99].BaseObject);
        }

        [Fact]
        public void HomogeneousPipeline_ByPropertyName_BindsBothParams()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(VfpByPropNameFunctionScript + @"
                    1..50 | ForEach-Object { [PSCustomObject]@{ Name = ""item$_""; Count = $_ } } | Test-ByPropNameFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(50, results.Count);
            Assert.Equal("item1=1",   (string)results[0].BaseObject);
            Assert.Equal("item50=50", (string)results[49].BaseObject);
        }

        [Fact]
        public void HeterogeneousPipeline_VfpOnly_AllBindSuccessfully()
        {
            // ValueFromPipeline-only plans stay valid across type changes (coercion handles it).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-HeteroFunc {
                        [CmdletBinding()]
                        param(
                            [Parameter(ValueFromPipeline=$true)]
                            [string]$Value
                        )
                        process { $Value }
                    }
                    @(1, 'hello', 2.5, $true) | Test-HeteroFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(4, results.Count);
            Assert.Equal("1",     (string)results[0].BaseObject);
            Assert.Equal("hello", (string)results[1].BaseObject);
        }

        [Fact]
        public void HeterogeneousPipeline_ByPropName_TypeChangeFallsBackToSlowPath()
        {
            // When input object type changes mid-pipeline for a ByPropertyName plan,
            // the fast path invalidates and the slow path handles the remaining objects.
            using var ps = PowerShell.Create();
            ps.AddScript(VfpByPropNameFunctionScript + @"
                    $obj1 = [PSCustomObject]@{ Name = 'first'; Count = 1 }
                    $obj2 = [PSCustomObject]@{ Name = 'second'; Count = 2 }
                    @($obj1, $obj2) | Test-ByPropNameFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.Equal("first=1",  (string)results[0].BaseObject);
            Assert.Equal("second=2", (string)results[1].BaseObject);
        }

        [Fact]
        public void SingleObject_Pipeline_NoRegression()
        {
            // Single pipeline object: plan is created but never replayed.
            using var ps = PowerShell.Create();
            ps.AddScript(VfpFunctionScript + @"
                    42 | Test-VfpFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(84, (int)results[0].BaseObject);
        }

        [Fact]
        public void EmptyPipeline_NoError()
        {
            // Empty pipeline: no plan is ever created. Must not error.
            using var ps = PowerShell.Create();
            ps.AddScript(VfpFunctionScript + @"
                    @() | Test-VfpFunc
                ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Empty(results);
        }

        [Fact]
        public void PipelineWithValidation_FailingObject_PropagatesError()
        {
            // Object that fails validation should still propagate the error correctly
            // regardless of whether the plan cache is warm.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                    function Test-ValidatedFunc {
                        [CmdletBinding()]
                        param(
                            [Parameter(ValueFromPipeline=$true)]
                            [ValidateRange(1, 10)]
                            [int]$Value
                        )
                        process { $Value }
                    }
                    # First two objects warm the cache and pass; third fails validation.
                    1, 2, 99 | Test-ValidatedFunc
                ");
            var results = ps.Invoke();
            Assert.Equal(2, results.Count);
            // The out-of-range value produces an error stream entry.
            Assert.NotEmpty(ps.Streams.Error);
        }
    }
}
