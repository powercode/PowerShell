// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for <c>ScriptParameterBinderController</c>. Verifies the script-specific
    /// binding behaviour: <c>$args</c> auto-variable population, default-value lifecycle, and the
    /// <c>WarnIfObsolete</c> warning path for non-CmdletBinding functions. All tests use
    /// <see cref="PowerShell.Create"/>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class ScriptParameterBindingTests
    {
        [Fact]
        public void Script_UnboundArgs_PopulateDollarArgs()
        {
            // For a non-CmdletBinding function, any argument that is not bound to a declared
            // parameter should appear in $args in the order it was passed.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script {
                    param([string]$Name)
                    $args.Count
                }
                # 'extra1' and 'extra2' are unbound; only $Name is declared.
                Test-Script -Name 'bound' 'extra1' 'extra2'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(2, results[0].BaseObject);
        }

        [Fact]
        public void Script_DollarArgs_ContainsOnlyUnboundArguments()
        {
            // $args must contain only the positional arguments that were NOT consumed by
            // declared parameters; explicitly bound named parameters must not appear in $args.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script {
                    param([string]$Name)
                    $args -join '|'
                }
                Test-Script -Name 'bound' 'unbound1' 'unbound2'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("unbound1|unbound2", (string)results[0].BaseObject);
        }

        [Fact]
        public void Script_DefaultValues_SetWhenNotProvided()
        {
            // A non-CmdletBinding function with a default value expression should produce
            // that default when the parameter is not supplied.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script {
                    param([string]$Greeting = 'hello')
                    $Greeting
                }
                Test-Script
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("hello", (string)results[0].BaseObject);
        }

        [Fact]
        public void Script_DefaultValues_OverriddenWhenProvided()
        {
            // When the caller explicitly supplies the parameter, the default must not be used.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script {
                    param([string]$Greeting = 'hello')
                    $Greeting
                }
                Test-Script -Greeting 'goodbye'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("goodbye", (string)results[0].BaseObject);
        }

        [Fact]
        public void Script_ObsoleteParameter_EmitsWarning()
        {
            // A parameter decorated with [Obsolete] on a non-CmdletBinding function must
            // trigger a warning through WarnIfObsolete when the parameter is used.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script {
                    param(
                        [Obsolete('Use -NewParam instead')]
                        [string]$OldParam
                    )
                    $OldParam
                }
                Test-Script -OldParam 'val'
            ");
            var results = ps.Invoke();
            // The result should still be returned despite the warning.
            Assert.Single(results);
            Assert.Equal("val", (string)results[0].BaseObject);
            // A warning about the obsolete parameter must appear in the warning stream.
            Assert.NotEmpty(ps.Streams.Warning);
        }

        [Fact]
        public void Script_ExtraPositionalArgs_InDollarArgs_PreserveOrder()
        {
            // Positional arguments that overflow declared parameters must appear in $args
            // in the same left-to-right order they were passed.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Script { $args -join ',' }
                Test-Script 'a' 'b' 'c'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("a,b,c", (string)results[0].BaseObject);
        }
    }
}
