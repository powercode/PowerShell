// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class PipelineParameterBindingTests
    {
        [Fact]
        public void Pipeline_ValueFromPipeline_NoCoercion_BindsDirectly()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipeline)] [int]$Value)
                    process { $Value }
                }
                42 | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(42, results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipeline_WithCoercion_StringToInt()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipeline)] [int]$Value)
                    process { $Value }
                }
                '42' | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(42, results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipelineByPropertyName_NoCoercion()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipelineByPropertyName)] [string]$Name)
                    process { $Name }
                }
                [pscustomobject]@{ Name = 'x' } | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("x", (string)results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipelineByPropertyName_WithCoercion()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipelineByPropertyName)] [int]$Count)
                    process { $Count }
                }
                [pscustomobject]@{ Count = '5' } | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(5, results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipelineByPropertyName_MatchesAlias()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Alias('CN')]
                        [Parameter(ValueFromPipelineByPropertyName)]
                        [string]$ComputerName
                    )
                    process { $ComputerName }
                }
                [pscustomobject]@{ CN = 'server01' } | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("server01", (string)results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_NoMatchingProperty_DoesNotBind()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipelineByPropertyName)] [string]$Name = 'default')
                    process { $Name }
                }
                [pscustomobject]@{ Other = 1 } | Test-Func
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
        }

        [Fact]
        public void Pipeline_MultipleObjects_ResetsDefaultsBetweenObjects()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [object]$InputObject,
                        [Parameter(ValueFromPipelineByPropertyName)] [string]$Name = 'unset'
                    )
                    process { $Name }
                }

                @(
                    [pscustomobject]@{ Name = 'a' }
                    [pscustomobject]@{ Other = 1 }
                    [pscustomobject]@{ Name = 'c' }
                ) | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(3, results.Count);
            Assert.Equal("a", (string)results[0].BaseObject);
            Assert.Equal("unset", (string)results[1].BaseObject);
            Assert.Equal("c", (string)results[2].BaseObject);
        }

        [Fact]
        public void Pipeline_BindingFailure_RestoresDefaults()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [object]$InputObject,
                        [Parameter(ValueFromPipelineByPropertyName)] [int]$Count = 7
                    )
                    process { $Count }
                }

                @(
                    [pscustomobject]@{ Count = 'bad' }
                    [pscustomobject]@{ Other = 1 }
                ) | Test-Func
            ");

            var results = ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(7, results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_CoercionFailure_FallsThroughToNextState()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)]
                        [int]$Value,

                        [Parameter(ValueFromPipelineByPropertyName)]
                        [int]$Count
                    )

                    process { $Count }
                }

                [pscustomobject]@{ Count = '5' } | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(5, results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_PrioritySet_BindsToPrioritizedSetFirst()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding(DefaultParameterSetName='ByString')]
                    param(
                        [Parameter(ParameterSetName='ByString', ValueFromPipeline)]
                        [string]$Value,

                        [Parameter(ParameterSetName='ByInt', ValueFromPipeline)]
                        [int]$Count
                    )

                    process { $PSCmdlet.ParameterSetName }
                }

                '5' | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByString", (string)results[0].BaseObject);
        }

        [Fact]
        public void Pipeline_ValueFromPipeline_TakesPrecedence_OverByPropertyName()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline=$true, ValueFromPipelineByPropertyName=$true)]
                        [string]$Name
                    )
                    process { $Name }
                }

                [pscustomobject]@{ Name = 'property-value' } | Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("property-value", (string)results[0].BaseObject);
        }
    }
}
