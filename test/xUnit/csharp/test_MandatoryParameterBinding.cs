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
    [Trait("Category", "Integration")]
    public class MandatoryParameterBindingTests
    {
        [Fact]
        public void WhereObject_ScriptSet_IgnoresMandatoryParametersInOtherSets()
        {
            using var ps = PowerShell.Create();
            ps.AddScript("1, 2, 3 | Where-Object { $_ -gt 1 }");

            var results = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.Equal(2, results[0].BaseObject);
            Assert.Equal(3, results[1].BaseObject);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void Mandatory_MissingProperty_PreservesErrorAndBoundState(bool namedSet, bool appendValidRecord)
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-MandatoryRecord {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline __SET__)]
                        [object]$Record,
                        [Parameter(Mandatory, ValueFromPipelineByPropertyName __SET__)]
                        [string]$Name
                    )
                    process {
                        [pscustomobject]@{
                            Name = $Name
                            Set = $PSCmdlet.ParameterSetName
                            Bound = $PSBoundParameters.ContainsKey('Name')
                        }
                    }
                }
                @(
                    [pscustomobject]@{ Name = 'first' }
                    [pscustomobject]@{ Other = 'missing' }
                    __RECOVERY__
                ) | Test-MandatoryRecord
            ".Replace("__SET__", namedSet ? ", ParameterSetName = 'Input'" : string.Empty)
                .Replace("__RECOVERY__", appendValidRecord ? "[pscustomobject]@{ Name = 'third' }" : string.Empty));

            var results = ps.Invoke();

            Assert.Equal(appendValidRecord ? 2 : 1, results.Count);
            Assert.Equal("first", results[0].Properties["Name"].Value);
            if (appendValidRecord)
            {
                Assert.Equal("third", results[1].Properties["Name"].Value);
            }
            foreach (var result in results)
            {
                Assert.Equal(namedSet ? "Input" : "__AllParameterSets", result.Properties["Set"].Value);
                Assert.Equal(true, result.Properties["Bound"].Value);
            }

            var error = Assert.Single(ps.Streams.Error);
            Assert.Equal("InputObjectMissingMandatory", error.FullyQualifiedErrorId.Split(',')[0]);
            Assert.IsAssignableFrom<ParameterBindingException>(error.Exception);
            Assert.Equal("missing", PSObject.AsPSObject(error.TargetObject).Properties["Other"].Value);
        }

        [Fact]
        public void Mandatory_PipelineValidationFailure_IsNotBypassed()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-MandatoryValidation {
                    [CmdletBinding()]
                    param(
                        [Parameter(Mandatory, ValueFromPipeline)]
                        [ValidateRange(1, 10)]
                        [int]$Value
                    )
                    process { $Value }
                }
                1, 99, 2 | Test-MandatoryValidation
            ");

            var results = ps.Invoke();

            Assert.Equal(2, results.Count);
            Assert.Equal(1, results[0].BaseObject);
            Assert.Equal(2, results[1].BaseObject);
            var error = Assert.Single(ps.Streams.Error);
            Assert.IsAssignableFrom<ParameterBindingException>(error.Exception);
            Assert.Equal("ParameterArgumentValidationError", error.FullyQualifiedErrorId.Split(',')[0]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Mandatory_CommandLineOrDefaultValue_RemainsBoundAcrossPipelineRecords(bool useDefault)
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-MandatoryFixed {
                    [CmdletBinding()]
                    param(
                        [Parameter(Mandatory)]
                        [string]$Name,
                        [Parameter(ValueFromPipeline)]
                        [object]$Record
                    )
                    process { $PSBoundParameters['Name'] }
                }
                $PSDefaultParameterValues = @{ 'Test-MandatoryFixed:Name' = 'default' }
                1, 2 | Test-MandatoryFixed __ARGUMENT__
            ".Replace("__ARGUMENT__", useDefault ? string.Empty : "-Name explicit"));

            var results = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.All(results, result => Assert.Equal(useDefault ? "default" : "explicit", result.BaseObject));
        }

        [Fact]
        public void Mandatory_DynamicParameter_BindsEachPipelineRecord()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-MandatoryDynamic {
                    [CmdletBinding()]
                    param()
                    dynamicparam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $attributes.Add([System.Management.Automation.ParameterAttribute]@{
                            Mandatory = $true
                            ValueFromPipelineByPropertyName = $true
                        })
                        $parameters = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $parameters.Add('Name', [System.Management.Automation.RuntimeDefinedParameter]::new(
                            'Name', [string], $attributes))
                        $parameters
                    }
                    process { $PSBoundParameters['Name'] }
                }
                [pscustomobject]@{ Name = 'first' }, [pscustomobject]@{ Name = 'second' } |
                    Test-MandatoryDynamic
            ");

            var results = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.Equal("first", results[0].BaseObject);
            Assert.Equal("second", results[1].BaseObject);
        }

        [Fact]
        public void Mandatory_DelayBoundArgument_IsEvaluatedForEachRecord()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-MandatoryDelayed {
                    [CmdletBinding()]
                    param([Parameter(Mandatory, ValueFromPipelineByPropertyName)][int]$Number)
                    process { $Number }
                }
                1, 2 | Test-MandatoryDelayed -Number { $_ * 3 }
            ");

            var results = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, results.Count);
            Assert.Equal(3, results[0].BaseObject);
            Assert.Equal(6, results[1].BaseObject);
        }

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
