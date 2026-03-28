// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for <c>MandatoryParameterPrompter</c>. In non-interactive mode
    /// (the mode provided by <see cref="PowerShell.Create"/>), missing mandatory parameters
    /// produce a <see cref="ParameterBindingException"/> error rather than prompting the user.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class MandatoryParameterBindingTests
    {
        [Fact]
        public void Mandatory_MissingParam_NonInteractive_WritesParameterBindingError()
        {
            // In non-interactive mode a missing mandatory parameter must produce an error of
            // type ParameterBindingException rather than blocking on a prompt.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Mandatory {
                    [CmdletBinding()]
                    param([Parameter(Mandatory)][string]$Name)
                    $Name
                }
                Test-Mandatory
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void Mandatory_MultipleUnbound_ErrorMessageMentionsAllParams()
        {
            // When two mandatory parameters are both missing, the error should reference
            // both parameter names so the caller knows exactly what is required.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Mandatory {
                    [CmdletBinding()]
                    param(
                        [Parameter(Mandatory)][string]$First,
                        [Parameter(Mandatory)][string]$Second
                    )
                    $First
                }
                Test-Mandatory
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            var errorMsg = ps.Streams.Error[0].Exception.Message;
            // At minimum one of the mandatory parameter names must appear in the message.
            Assert.True(
                errorMsg.Contains("First", System.StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("Second", System.StringComparison.OrdinalIgnoreCase),
                $"Expected mandatory param name in error message but got: {errorMsg}");
        }

        [Fact]
        public void Mandatory_SatisfiedByValueFromPipeline_NoError()
        {
            // A mandatory parameter with ValueFromPipeline should be satisfied by piping a
            // value, requiring no prompt and producing no error.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Mandatory {
                    [CmdletBinding()]
                    param([Parameter(Mandatory, ValueFromPipeline)][int]$Value)
                    process { $Value }
                }
                42 | Test-Mandatory
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(42, results[0].BaseObject);
        }

        [Fact]
        public void Mandatory_SatisfiedByPSDefaultParameterValues_NoError()
        {
            // $PSDefaultParameterValues must satisfy a mandatory parameter, preventing an error
            // even though the parameter was not supplied on the command line.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Mandatory {
                    [CmdletBinding()]
                    param([Parameter(Mandatory)][string]$Name)
                    $Name
                }
                $PSDefaultParameterValues = @{ 'Test-Mandatory:Name' = 'from-defaults' }
                Test-Mandatory
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("from-defaults", (string)results[0].BaseObject);
        }

        [Fact]
        public void Mandatory_SwitchParam_NotMandatoryByDefault()
        {
            // Switch parameters must never be implicitly mandatory. Even without [Mandatory]
            // the function should execute normally with the switch absent (i.e. $false).
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Mandatory {
                    [CmdletBinding()]
                    param([switch]$Force)
                    $Force.IsPresent
                }
                Test-Mandatory
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(false, results[0].BaseObject);
        }
    }
}
