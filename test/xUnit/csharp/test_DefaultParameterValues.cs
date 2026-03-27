// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    public class DefaultParameterValuesTests
    {
        [Fact]
        public void DefaultValues_SimpleBinding_AppliesNamedDefault()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-Func:Name'] = 'hello'
                function Test-Func {
                    [CmdletBinding()]
                    param([string]$Name)
                    $Name
                }
                Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("hello", (string)results[0].BaseObject);
        }

        [Fact]
        public void DefaultValues_MandatoryParam_SatisfiedByDefault()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-Func:Name'] = 'defaultName'
                function Test-Func {
                    [CmdletBinding()]
                    param([Parameter(Mandatory)] [string]$Name)
                    $Name
                }
                Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("defaultName", (string)results[0].BaseObject);
        }

        [Fact]
        public void DefaultValues_DynamicParam_BoundAfterDiscovery()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-DynamicDefault:Mode'] = 'Auto'
                function Test-DynamicDefault {
                    [CmdletBinding()]
                    param()

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $attributes.Add([System.Management.Automation.ParameterAttribute]::new())
                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Mode', [string], $attributes)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Mode', $rdp)
                        return $dict
                    }

                    process { $PSBoundParameters['Mode'] }
                }

                Test-DynamicDefault
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("Auto", (string)results[0].BaseObject);
        }

        [Fact]
        public void DefaultValues_WildcardCmdletName_MatchesMultiple()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['*:Name'] = 'global-default'
                function Test-One {
                    [CmdletBinding()]
                    param([string]$Name)
                    $Name
                }
                function Test-Two {
                    [CmdletBinding()]
                    param([string]$Name)
                    $Name
                }
                @(Test-One; Test-Two)
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.Equal("global-default", (string)results[0].BaseObject);
            Assert.Equal("global-default", (string)results[1].BaseObject);
        }

        [Fact]
        public void DefaultValues_OverriddenByExplicitArg_ExplicitWins()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-Func:Name'] = 'default'
                function Test-Func {
                    [CmdletBinding()]
                    param([string]$Name)
                    $Name
                }
                Test-Func -Name 'explicit'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("explicit", (string)results[0].BaseObject);
        }

        [Fact]
        public void DefaultValues_PositionalThenDefault_BothBind()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-Func:Tail'] = 'tail-default'
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$Head,
                        [string]$Tail
                    )
                    '{0}|{1}' -f $Head, $Tail
                }
                Test-Func 'head-value'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("head-value|tail-default", (string)results[0].BaseObject);
        }

        [Fact]
        public void DefaultValues_Disabled_KeySetToDisable_SkipsBinding()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Disabled'] = $true
                $PSDefaultParameterValues['Test-Func:Name'] = 'should-not-apply'
                function Test-Func {
                    [CmdletBinding()]
                    param([string]$Name = 'fallback')
                    $Name
                }
                Test-Func
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("fallback", (string)results[0].BaseObject);
        }
    }
}
