// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class DelayBindScriptBlockTests
    {
        [Fact]
        public void DelayBind_ScriptBlockToFileInfoParam_WaitsForPipeline()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [object]$InputObject,
                        [System.IO.FileInfo]$Path
                    )
                    process { $Path.Name }
                }
                'sample.txt' | Test-Func -Path { $_ }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_ScriptBlockToObjectParam_BindsImmediately()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([object]$Value)
                    $Value.GetType().Name
                }
                Test-Func -Value { 42 }
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ScriptBlock", (string)results[0].BaseObject);
        }

        [Fact]
        public void DelayBind_ScriptBlockToScriptBlockParam_BindsImmediately()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([scriptblock]$Block)
                    $Block.GetType().Name
                }
                Test-Func -Block { 42 }
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ScriptBlock", (string)results[0].BaseObject);
        }

        [Fact]
        public void DelayBind_PipelineInput_InvokesScriptBlockPerObject()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [int]$InputObject,
                        [int]$Value
                    )
                    process { $Value }
                }
                1..3 | Test-Func -Value { $_ * 2 }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_ScriptBlockReturnsEmpty_ThrowsBindingException()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [int]$InputObject,
                        [int]$Value
                    )
                    process { $Value }
                }
                1 | Test-Func -Value { @() }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_ScriptBlockThrowsRuntime_ThrowsBindingException()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [int]$InputObject,
                        [int]$Value
                    )
                    process { $Value }
                }
                1 | Test-Func -Value { throw 'boom' }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_NoPipelineInput_ThrowsBindingException()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param([int]$Value)
                    $Value
                }
                Test-Func -Value { $_ }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_MultipleDelayBound_AllResolvedPerObject()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [int]$InputObject,
                        [string]$Name,
                        [int]$Value
                    )
                    process { '{0}:{1}' -f $Name, $Value }
                }
                1..2 | Test-Func -Name { $_ } -Value { $_ * 10 }
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DelayBind_ScriptBlockReturnsSingleValue_UnwrapsFromCollection()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Func {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline)] [int]$InputObject,
                        [string]$Name
                    )
                    process { $Name }
                }
                1 | Test-Func -Name { ,($_.ToString()) }
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Contains("$_.ToString", (string)results[0].BaseObject, StringComparison.Ordinal);
        }
    }
}
