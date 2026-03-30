// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class DynamicParameterBindingTests
    {
        [Fact]
        public void DynamicParam_CmdletWithDynamicParams_BindsDynamicNamedParam()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $path = Join-Path ([System.IO.Path]::GetTempPath()) ('pb-dyn-' + [guid]::NewGuid().ToString('N') + '.txt')
                'a,b,c' | Set-Content -Path $path -NoNewline
                try {
                    (Get-Content -Path $path -Delimiter ',').Count
                }
                finally {
                    Remove-Item -Path $path -ErrorAction SilentlyContinue
                }
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(3, results[0].BaseObject);
        }

        [Fact]
        public void DynamicParam_FunctionWithDynamicBlock_BindsDynamicParam()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Dynamic {
                    [CmdletBinding()]
                    param()

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $paramAttr = [System.Management.Automation.ParameterAttribute]::new()
                        $attributes.Add($paramAttr)

                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Mode', [string], $attributes)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Mode', $rdp)
                        return $dict
                    }

                    process { $PSBoundParameters['Mode'] }
                }

                Test-Dynamic -Mode 'Fast'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("Fast", (string)results[0].BaseObject);
        }

        [Fact]
        public void DynamicParam_NameConflict_DynamicAndStatic_ThrowsMetadataException()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-DynamicConflict {
                    [CmdletBinding()]
                    param([string]$Name)

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $paramAttr = [System.Management.Automation.ParameterAttribute]::new()
                        $attributes.Add($paramAttr)

                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Name', [string], $attributes)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Name', $rdp)
                        return $dict
                    }
                }

                Test-DynamicConflict -Name 'x'
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<MetadataException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DynamicParam_AliasConflict_ThrowsMetadataException()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-DynamicAliasConflict {
                    [CmdletBinding()]
                    param([string]$Name)

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $paramAttr = [System.Management.Automation.ParameterAttribute]::new()
                        $attributes.Add($paramAttr)
                        $attributes.Add([System.Management.Automation.AliasAttribute]::new('Name'))

                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Other', [string], $attributes)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Other', $rdp)
                        return $dict
                    }
                }

                Test-DynamicAliasConflict -Name 'x' -Other 'y'
            ");

            ps.Invoke();
            Assert.NotEmpty(ps.Streams.Error);
            Assert.IsAssignableFrom<MetadataException>(ps.Streams.Error[0].Exception);
        }

        [Fact]
        public void DynamicParam_DefaultValues_AppliedAfterDynamicDiscovery()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSDefaultParameterValues['Test-DynamicDefault:Mode'] = 'Auto'

                function Test-DynamicDefault {
                    [CmdletBinding()]
                    param()

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $paramAttr = [System.Management.Automation.ParameterAttribute]::new()
                        $attributes.Add($paramAttr)

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
        public void DynamicParam_PositionalBinding_IncludesDynamicParams()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-DynamicPosition {
                    [CmdletBinding()]
                    param()

                    DynamicParam {
                        $attributes = [System.Collections.ObjectModel.Collection[System.Attribute]]::new()
                        $paramAttr = [System.Management.Automation.ParameterAttribute]::new()
                        $paramAttr.Position = 0
                        $attributes.Add($paramAttr)

                        $rdp = [System.Management.Automation.RuntimeDefinedParameter]::new('Mode', [string], $attributes)
                        $dict = [System.Management.Automation.RuntimeDefinedParameterDictionary]::new()
                        $dict.Add('Mode', $rdp)
                        return $dict
                    }

                    process { $PSBoundParameters['Mode'] }
                }

                Test-DynamicPosition 'Positional'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("Positional", (string)results[0].BaseObject);
        }
    }
}
