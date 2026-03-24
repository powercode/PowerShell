// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    public class ParameterBindingIntegrationTests
    {
        [Fact]
        public void NamedParameter_ExactMatch_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -Path 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_PrefixMatch_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -Pa 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_AmbiguousPrefix_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path, [string]$Process)
                    }
                    Test-Func -P 'value'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
                Assert.Contains("ambiguous", ps.Streams.Error[0].Exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void NamedParameter_Alias_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([Alias('CN')][string]$ComputerName)
                        $ComputerName
                    }
                    Test-Func -CN 'server1'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("server1", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_NotFound_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                    }
                    Test-Func -NonExistent 'value'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
            }
        }

        [Fact]
        public void NamedParameter_CaseInsensitive_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -path 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_AlreadyBound_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                    }
                    Test-Func -Path 'first' -Path 'second'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
            }
        }

        [Fact]
        public void SwitchParameter_NoArgument_IsPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Force)
                        $Force.IsPresent
                    }
                    Test-Func -Force
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(true, results[0].BaseObject);
            }
        }

        [Fact]
        public void SwitchParameter_ExplicitFalse_NotPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Force)
                        $Force.IsPresent
                    }
                    Test-Func -Force:$false
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(false, results[0].BaseObject);
            }
        }
    }

    public class PositionalBindingIntegrationTests
    {
        [Fact]
        public void Positional_SingleSet_BindsByPosition()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(Position=1)] [string]$Second
                        )
                        ""$First-$Second""
                    }
                    Test-Func 'hello' 'world'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello-world", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_SkippedPosition_BindsCorrectly()
        {
            using (var ps = PowerShell.Create())
            {
                // Position=0 and Position=2 — passing two args binds to 0 and 2 in order.
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$A,
                            [Parameter(Position=2)] [string]$C
                        )
                        ""$A-$C""
                    }
                    Test-Func 'first' 'third'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("first-third", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_NamedThenPositional_FillsRemaining()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(Position=1)] [string]$Second
                        )
                        ""$First-$Second""
                    }
                    Test-Func -Second 'world' 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello-world", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_VRACaptures_Overflow()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(ValueFromRemainingArguments=$true)] [string[]]$Rest
                        )
                        [PSCustomObject]@{ First=$First; Rest=$Rest }
                    }
                    $r = Test-Func 'a' 'b' 'c'
                    $r.First + '-' + ($r.Rest -join ',')
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("a-b,c", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_MultiSet_ParamAppearsInBothSets_BindsOnce()
        {
            // Regression test for issue #2212:
            // A parameter that appears at different positions in different parameter sets
            // should only be bound once (to the first matching position).
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding(DefaultParameterSetName='One')]
                        param(
                            [Parameter(ParameterSetName='One', Position=0)]
                            [Parameter(ParameterSetName='Two', Position=1)]
                            [string]$First,
                            [Parameter(ParameterSetName='One', Position=1)]
                            [Parameter(ParameterSetName='Two', Position=0)]
                            [string]$Second
                        )
                        ""$First|$Second""
                    }
                    Test-Func 'Hello' 'World'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                // In set 'One' (default): First=pos0, Second=pos1 → "Hello|World"
                Assert.Equal("Hello|World", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_ExplicitSetSelection_UsesSetPositions()
        {
            // Selecting set 'Two' via a set-differentiating named parameter.
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding(DefaultParameterSetName='One')]
                        param(
                            [Parameter(ParameterSetName='One', Position=0)]
                            [Parameter(ParameterSetName='Two', Position=1)]
                            [string]$First,
                            [Parameter(ParameterSetName='One', Position=1)]
                            [Parameter(ParameterSetName='Two', Position=0)]
                            [string]$Second,
                            [Parameter(ParameterSetName='Two', Mandatory=$false)]
                            [switch]$UseTwo
                        )
                        ""$First|$Second""
                    }
                    Test-Func -UseTwo 'Hello' 'World'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                // In set 'Two': Second=pos0, First=pos1 → First='World', Second='Hello'
                Assert.Equal("World|Hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_Overflow_NoVRA_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([Parameter(Position=0)] [string]$A)
                        $A
                    }
                    Test-Func 'first' 'extra'
                ");
                var results = ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }
    }
}
