// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for the binding orchestration layer:
    /// <c>CmdletParameterBinderController</c> (<c>VerifyArgumentsProcessed</c>,
    /// <c>IsParameterScriptBlockBindable</c>), <c>ParameterBinderBase</c>
    /// (<c>ApplyArgumentTransformations</c>, <c>RunValidationPipeline</c>), and
    /// <c>DynamicParameterHandler</c>. All tests use <see cref="PowerShell.Create"/>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class BindingOrchestrationTests
    {
        [Fact]
        public void Orchestration_UnknownNamedParam_WritesCannotBeFoundError()
        {
            // VerifyArgumentsProcessed must report an error when a named parameter token
            // is present in the argument list but does not match any parameter.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param([string]$Known)
                }
                Test-Orch -BadParam 'value'
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
            Assert.Contains("BadParam", ps.Streams.Error[0].Exception.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Orchestration_TooManyPositionalArgs_WritesPositionalOverflowError()
        {
            // When more positional arguments are provided than there are positional slots,
            // VerifyArgumentsProcessed must produce an overflow error.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param([Parameter(Position=0)][string]$Only)
                }
                Test-Orch 'first' 'overflow'
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void Orchestration_ScriptBlockToObjectParam_BindsAsObject()
        {
            // IsParameterScriptBlockBindable returns true for [object] parameters, so the
            // ScriptBlock must be stored as-is (not invoked) when bound to [object].
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param([object]$Handler)
                    $Handler -is [scriptblock]
                }
                Test-Orch -Handler { 'script' }
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(true, results[0].BaseObject);
        }

        [Fact]
        public void Orchestration_ScriptBlockToTypedStringParam_InvokedAndResultBound()
        {
            // When a ScriptBlock is bound to a [string] parameter, the engine must invoke
            // it via the delay-bind mechanism and coerce the result to string.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param([Parameter(ValueFromPipeline)][string]$Name)
                    process { $Name }
                }
                'hello' | Test-Orch -Name { 'computed-' + $_ }
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("computed-hello", (string)results[0].BaseObject);
        }

        [Fact]
        public void Orchestration_ArgumentTransformAttribute_TransformsValueBeforeBinding()
        {
            // ApplyArgumentTransformations must run [ArgumentTransformation] attributes
            // before the value is coerced and validated.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                class UpperCaseTransform : System.Management.Automation.ArgumentTransformationAttribute {
                    [object] Transform(
                        [System.Management.Automation.EngineIntrinsics]$ctx,
                        [object]$val) {
                        return ($val -as [string]).ToUpper()
                    }
                }

                function Test-Orch {
                    [CmdletBinding()]
                    param([UpperCaseTransform()][string]$Text)
                    $Text
                }
                Test-Orch -Text 'hello'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("HELLO", (string)results[0].BaseObject);
        }

        [Fact]
        public void Orchestration_DynamicParams_BoundAfterDiscovery()
        {
            // DynamicParameterHandler must merge dynamic parameters into the metadata and
            // allow them to be bound normally in the same invocation.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param()
                    DynamicParam {
                        $attrs = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $attrs.Add([System.Management.Automation.ParameterAttribute]::new())
                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Mode', [string], $attrs)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Mode', $rdp)
                        return $dict
                    }
                    process { $PSBoundParameters['Mode'] }
                }
                Test-Orch -Mode 'fast'
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("fast", (string)results[0].BaseObject);
        }

        [Fact]
        public void Orchestration_ValidateAttribute_ErrorPropagatesFromValidationPipeline()
        {
            // RunValidationPipeline must surface a ValidationMetadataException when a
            // ValidateSet attribute rejects the supplied value.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Orch {
                    [CmdletBinding()]
                    param([ValidateSet('a','b','c')][string]$Choice)
                    $Choice
                }
                Test-Orch -Choice 'invalid'
            ");
            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            // The inner exception must indicate the set violation.
            var ex = ps.Streams.Error[0].Exception;
            Assert.True(
                ex is ParameterBindingException ||
                ex is System.Management.Automation.ValidationMetadataException,
                $"Unexpected exception type: {ex?.GetType().Name}");
        }
    }
}
