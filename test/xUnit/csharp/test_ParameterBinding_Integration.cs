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
}
