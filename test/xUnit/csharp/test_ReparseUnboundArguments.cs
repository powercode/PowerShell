// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for <c>ReparseUnboundArguments</c> (inside
    /// <c>ParameterBinderController</c>). Verifies edge-cases in parameter–value pairing and the
    /// "argument looks like a parameter" heuristic. All tests use <see cref="PowerShell.Create"/>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class ReparseUnboundArgumentsTests
    {
        [Fact]
        public void Reparse_ParamFollowedByValue_PairedCorrectly()
        {
            // The most common case: -Name value pairs with no ambiguity.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Reparse {
                    [CmdletBinding()]
                    param([string]$Name)
                    $Name
                }
                Test-Reparse -Name 'hello'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("hello", (string)results[0].BaseObject);
        }

        [Fact]
        public void Reparse_SwitchThenNamedParam_SwitchGetsNoValue()
        {
            // When a switch parameter is followed by another named parameter, the switch
            // must claim no value and the next token must bind to the second parameter.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Reparse {
                    [CmdletBinding()]
                    param(
                        [switch]$Force,
                        [string]$Name
                    )
                    '{0}:{1}' -f $Force.IsPresent, $Name
                }
                Test-Reparse -Force -Name 'value'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("True:value", (string)results[0].BaseObject);
        }

        [Fact]
        public void Reparse_TrailingParamWithNoValue_WritesError()
        {
            // A named parameter at the end of the argument list with no value following it
            // must produce a ParameterBindingException (missing argument).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Reparse {
                    [CmdletBinding()]
                    param([string]$Name)
                }
                Test-Reparse -Name
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void Reparse_UnknownParamWithVRA_LandsInValueFromRemainingArguments()
        {
            // An unknown named parameter token that looks like -Name but is unrecognised
            // should be collected by a ValueFromRemainingArguments parameter when present.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Reparse {
                    [CmdletBinding()]
                    param([Parameter(ValueFromRemainingArguments)][string[]]$Rest)
                    $Rest -join '|'
                }
                Test-Reparse 'alpha' 'beta' 'gamma'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("alpha|beta|gamma", (string)results[0].BaseObject);
        }

        [Fact]
        public void Reparse_DashAloneAsValue_TreatedAsLiteralValue()
        {
            // A lone '-' token that follows a named parameter should be treated as the
            // value for that parameter, not as a new parameter token.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Reparse {
                    [CmdletBinding()]
                    param([string]$Sep)
                    $Sep
                }
                Test-Reparse -Sep '-'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("-", (string)results[0].BaseObject);
        }
    }
}
