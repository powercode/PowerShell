// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for the 4-pass positional parameter binding algorithm inside
    /// <c>BindPositionalParameters</c> and <c>BindPositionalParametersInSet</c>:
    /// <list type="number">
    ///   <item>Default set, no coercion</item>
    ///   <item>Other sets, no coercion</item>
    ///   <item>Default set, with coercion</item>
    ///   <item>Other sets, with coercion</item>
    /// </list>
    /// Also covers the already-bound-skip fix (issue #2212 regression).
    /// All tests use <see cref="PowerShell.Create"/>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class BindPositionalParametersAlgorithmTests
    {
        [Fact]
        public void Positional_DefaultSet_PreferredOverOtherSetForExactTypeMatch()
        {
            // A positional argument that exactly matches the type of the default-set parameter
            // must bind to the default set in pass 1 (no coercion, default set).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding(DefaultParameterSetName='ByName')]
                    param(
                        [Parameter(ParameterSetName='ByName',  Position=0)] [string]$Name,
                        [Parameter(ParameterSetName='ByIndex', Position=0)] [int]$Index
                    )
                    $PSCmdlet.ParameterSetName
                }
                Test-Pos 'hello'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByName", (string)results[0].BaseObject);
        }

        [Fact]
        public void Positional_OtherSet_BoundWhenDefaultSetTypeMismatch()
        {
            // When the positional argument cannot bind to the default set without coercion,
            // the binder falls through to other sets and binds there.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding(DefaultParameterSetName='ByName')]
                    param(
                        [Parameter(ParameterSetName='ByName',  Position=0)] [string]$Name,
                        [Parameter(ParameterSetName='ByIndex', Position=0)] [int]$Index
                    )
                    $PSCmdlet.ParameterSetName
                }
                # Passing an integer favours ByIndex because int matches exactly,
                # while binding to 'ByName' [string] would require coercion.
                Test-Pos 7
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ByIndex", (string)results[0].BaseObject);
        }

        [Fact]
        public void Positional_AlreadyBoundByName_SkippedDuringPositionalPass()
        {
            // A parameter that was already bound by name must not be re-bound during the
            // positional pass (regression guard for issue #2212).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$First,
                        [Parameter(Position=1)] [string]$Second
                    )
                    '{0}|{1}' -f $First, $Second
                }
                # -First is bound by name before the positional pass.
                # 'pos-value' should fall to $Second (position 1), not rebind to $First.
                Test-Pos -First 'named' 'pos-value'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("named|pos-value", (string)results[0].BaseObject);
        }

        [Fact]
        public void Positional_MixedNamedAndPositional_FillsRemainingPositionSlots()
        {
            // Named + positional arguments together should fill all position slots correctly;
            // the positional pass should skip already-named positions.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding()]
                    param(
                        [Parameter(Position=0)] [string]$A,
                        [Parameter(Position=1)] [string]$B,
                        [Parameter(Position=2)] [string]$C
                    )
                    '{0},{1},{2}' -f $A, $B, $C
                }
                Test-Pos -B 'explicit-b' 'pos-a' 'pos-c'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("pos-a,explicit-b,pos-c", (string)results[0].BaseObject);
        }

        [Fact]
        public void Positional_TooManyPositionalArgs_WritesError()
        {
            // When more positional arguments are supplied than there are declared positions,
            // a ParameterBindingException must be written to the error stream.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding()]
                    param([Parameter(Position=0)][string]$Only)
                }
                Test-Pos 'val1' 'overflow'
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void Positional_DefaultSet_CoercionFallback_Binds()
        {
            // If pass 1 (no coercion) fails for the default set, pass 3 (with coercion)
            // should bind the argument via type coercion.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Pos {
                    [CmdletBinding()]
                    param([Parameter(Position=0)][int]$Count)
                    $Count
                }
                # Pass '5' (string) which requires coercion to int.
                Test-Pos '5'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(5, results[0].BaseObject);
        }
    }
}
