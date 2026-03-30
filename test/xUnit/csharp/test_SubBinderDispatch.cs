// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class SubBinderDispatchTests
    {
        [Fact]
        public void Dispatch_CommonParameter_Verbose_BindsToCommonBinder()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-VerboseBinding {
                    [CmdletBinding()]
                    param()
                    process { Write-Verbose 'verbose-message' }
                }

                Test-VerboseBinding -Verbose
            ");

            ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.NotEmpty(ps.Streams.Verbose);
        }

        [Fact]
        public void Dispatch_ShouldProcessParam_WhatIf_BindsCorrectly()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-ShouldProcess {
                    [CmdletBinding(SupportsShouldProcess)]
                    param()

                    if ($PSCmdlet.ShouldProcess('target')) {
                        'executed'
                    }
                    else {
                        'whatif'
                    }
                }

                Test-ShouldProcess -WhatIf
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("whatif", (string)results[0].BaseObject);
        }

        [Fact]
        public void Dispatch_PagingParam_First_BindsCorrectly()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Paging {
                    [CmdletBinding(SupportsPaging)]
                    param()

                    process {
                        $items = 1..10
                        if ($PSCmdlet.PagingParameters.First -gt 0) {
                            $items = $items | Select-Object -First $PSCmdlet.PagingParameters.First
                        }

                        $items
                    }
                }

                (Test-Paging -First 3 | Measure-Object).Count
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(3, results[0].BaseObject);
        }

        [Fact]
        public void Dispatch_ObsoleteParam_GeneratesWarningRecord()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-Obsolete {
                    [CmdletBinding()]
                    param([Obsolete('Use NewName instead.')] [string]$OldName)
                    $OldName
                }

                Test-Obsolete -OldName 'value'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.NotEmpty(ps.Streams.Warning);
        }

        [Fact]
        public void Dispatch_SuccessfulBind_RemovesFromUnbound_AddsToBound()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-BoundParameters {
                    [CmdletBinding()]
                    param([string]$Name)

                    $PSBoundParameters.ContainsKey('Name')
                }

                Test-BoundParameters -Name 'item'
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(true, results[0].BaseObject);
        }

        [Fact]
        public void Dispatch_NarrowsParameterSetFlag_OnSuccess()
        {
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                function Test-ParameterSet {
                    [CmdletBinding(DefaultParameterSetName='ByName')]
                    param(
                        [Parameter(ParameterSetName='ByName')] [string]$Name,
                        [Parameter(ParameterSetName='ById')] [int]$Id
                    )

                    $PSCmdlet.ParameterSetName
                }

                Test-ParameterSet -Id 5
            ");

            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("ById", (string)results[0].BaseObject);
        }
    }
}
