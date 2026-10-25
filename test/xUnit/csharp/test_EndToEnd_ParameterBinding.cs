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
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet 'alpha' 'beta'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("ByName:alpha", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void MultiSet_ExplicitNamedSetSelection_BindsCorrectly()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet -First 'x' -Index 7
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("ByIndex:x", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void MultiSet_AmbiguousSinglePositional_UsesDefaultSet()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(MultiSetFunctionScript + @"
                    Test-MultiSet 'onlyFirst'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("ByName:onlyFirst", (string)results[0].BaseObject);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    public class EndToEnd_CoercionPlusValidationTests
    {
        [Fact]
        public void Int_String_CoercedAndValidated_Passes()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Int_String_CoercedAndValidated_OutOfRange_Fails()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Array_Parameter_ValidateCount_Passes()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Array_Parameter_ValidateCount_TooFew_Fails()
        {
            using (var ps = PowerShell.Create())
            {
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
    }

    [Trait("Category", "ParameterBinding")]
    public class EndToEnd_PositionalPlusSwitchTests
    {
        [Fact]
        public void Positional_PlusSwitchFlag_BothBind()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Positional_NamedOverride_IgnoresPosition()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Switch_ColonFalse_IsExplicitlyFalse_PositionalStillBinds()
        {
            using (var ps = PowerShell.Create())
            {
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
    }

    [Trait("Category", "ParameterBinding")]
    public class EndToEnd_PipelineBindingTests
    {
        [Fact]
        public void Pipeline_ValueFromPipeline_Binds()
        {
            using (var ps = PowerShell.Create())
            {
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
        }

        [Fact]
        public void Pipeline_ValueFromPipeline_WithValidation_PassesValidValue()
        {
            using (var ps = PowerShell.Create())
            {
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
    }
}
