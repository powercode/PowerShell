// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    public class RemainingArgumentsTests
    {
        [Fact]
        public void VRA_OverflowArgs_CapturedByVRAParam()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$First,
                        [Parameter(ValueFromRemainingArguments)] [string[]]$Rest
                    )
                    '{0}|{1}' -f $First, ($Rest -join ',')
                }
                Test-Func 'a' 'b' 'c' 'd'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("a|b,c,d", (string)results[0].BaseObject);
        }

        [Fact]
        public void VRA_NoOverflow_VRAIsEmpty()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$First,
                        [Parameter(ValueFromRemainingArguments)] [string[]]$Rest
                    )
                    if ($null -eq $Rest -or $Rest.Count -eq 0) { 'empty' } else { 'not-empty' }
                }
                Test-Func 'a'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("empty", (string)results[0].BaseObject);
        }

        [Fact]
        public void VRA_MultipleVRACandidates_InSelectedSet_Binds()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding(DefaultParameterSetName='SetA')]
                    param(
                        [Parameter(ParameterSetName='SetA', Position=0)]
                        [string]$Name,

                        [Parameter(ParameterSetName='SetA', ValueFromRemainingArguments)]
                        [string[]]$SetARest,

                        [Parameter(ParameterSetName='SetB')]
                        [switch]$UseB,

                        [Parameter(ParameterSetName='SetB', ValueFromRemainingArguments)]
                        [string[]]$SetBRest
                    )

                    if ($PSCmdlet.ParameterSetName -eq 'SetA') {
                        return $SetARest -join ','
                    }

                    return $SetBRest -join ','
                }

                Test-Func 'x' 'one' 'two'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("one,two", (string)results[0].BaseObject);
        }

        [Fact]
        public void VRA_MixedNamedAndPositional_OverflowGoesToVRA()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$First,
                        [string]$Label,
                        [Parameter(ValueFromRemainingArguments)] [string[]]$Rest
                    )
                    '{0}|{1}|{2}' -f $First, $Label, ($Rest -join ',')
                }
                Test-Func 'a' -Label 'tag' 'b' 'c'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("a|tag|b,c", (string)results[0].BaseObject);
        }

        [Fact]
        public void VRA_NoVRAParam_OverflowThrowsError()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(Position=0)] [string]$First)
                    $First
                }
                Test-Func 'a' 'b'
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }
    }
}
