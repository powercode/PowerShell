// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for hashtable and array splatting parameter binding.
    /// Covers <c>InitUnboundArguments</c>, the <c>FromHashtableSplatting</c> flag, and the
    /// explicit-parameter-supersedes-splatted rule. All tests use <see cref="PowerShell.Create"/>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class SplattingTests
    {
        [Fact]
        public void Splatting_Hashtable_BindsNamedParameters()
        {
            // Verifies that a hashtable passed with @ splatting binds each key as a named
            // parameter of the target command.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param([string]$First, [string]$Second)
                    '{0}|{1}' -f $First, $Second
                }
                $splat = @{ First = 'a'; Second = 'b' }
                Test-Splat @splat
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("a|b", (string)results[0].BaseObject);
        }

        [Fact]
        public void Splatting_Hashtable_ExplicitParamSupersedes()
        {
            // Verifies that an explicitly supplied named parameter takes priority over the
            // same key coming from a splatted hashtable (FromHashtableSplatting flag).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param([string]$Path)
                    $Path
                }
                $splat = @{ Path = 'from-splat' }
                Test-Splat @splat -Path 'explicit'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("explicit", (string)results[0].BaseObject);
        }

        [Fact]
        public void Splatting_Array_BindsPositionally()
        {
            // Verifies that an array passed with @ splatting binds elements to positional
            // parameters in declaration order.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$First,
                        [Parameter(Position=1)] [string]$Second
                    )
                    '{0}|{1}' -f $First, $Second
                }
                $splat = @('x', 'y')
                Test-Splat @splat
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("x|y", (string)results[0].BaseObject);
        }

        [Fact]
        public void Splatting_CombinedHashAndExplicit_ExplicitWins()
        {
            // Verifies that when a key exists in the splatted hashtable AND is also supplied
            // explicitly, the explicit value is the one that the function receives.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param([string]$Name, [string]$Value)
                    '{0}={1}' -f $Name, $Value
                }
                $splat = @{ Name = 'splat-name'; Value = 'splat-value' }
                Test-Splat @splat -Name 'explicit-name'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("explicit-name=splat-value", (string)results[0].BaseObject);
        }

        [Fact]
        public void Splatting_EmptyHashtable_BindsNoParameters()
        {
            // Verifies that splatting an empty hashtable results in no error; all parameters
            // fall back to their defaults.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param([string]$Path = 'default')
                    $Path
                }
                $splat = @{}
                Test-Splat @splat
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("default", (string)results[0].BaseObject);
        }

        [Fact]
        public void Splatting_InvalidParamName_WritesBindingError()
        {
            // Verifies that a hashtable key that does not match any parameter produces a
            // ParameterBindingException in the error stream.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param([string]$Known)
                }
                $splat = @{ BadParam = 'oops' }
                Test-Splat @splat
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void Splatting_Array_MixedIntAndString_BindsWithCoercion()
        {
            // Verifies that array splatting with mixed element types causes the binder to
            // coerce each element to the declared parameter type.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Splat {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$Label,
                        [Parameter(Position=1)] [int]$Count
                    )
                    '{0}:{1}' -f $Label, $Count
                }
                # Both elements need coercion: int 42 coerced to string, '7' coerced to int.
                $splat = @('hello', '7')
                Test-Splat @splat
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("hello:7", (string)results[0].BaseObject);
        }
    }
}
