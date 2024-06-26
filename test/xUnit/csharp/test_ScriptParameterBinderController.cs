// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Host;
using Xunit;
using NSubstitute;
using System.Management.Automation.Runspaces;

namespace PSTests.Parallel;

public class ScriptParameterBinderControllerTests
{
    [Fact]
    public void CanBindParameters()
    {

        var script = """
            function Test-Parameter {
                [CmdletBinding()]
                param
                (
                    [Parameter(ParameterSetName = 'Two', Position = 0)]
                    [Parameter(ParameterSetName = 'One', Position = 1)]
                    [string]
                    $First,
                                             
                    [Parameter(ParameterSetName = 'Two', Position = 1)]
                    [string]
                    $Second
                )
                                             
                "ParameterSet " + $PSCmdlet.ParameterSetName
                $PSBoundParameters
            }
            """;

        var host = Substitute.For<PSHost>();
        var iss = InitialSessionState.Create();
        var engine = new AutomationEngine(host, iss);

        var scriptBlock = engine.ParseScriptBlock(script, false);

        InternalCommand command = null;
        SessionStateScope localScope = new SessionStateScope(null);

        var invocationInfo = new InvocationInfo(command);

        var controller = new ScriptParameterBinderController(
            scriptBlock,
            invocationInfo,
            engine.Context,
            command,
            localScope
            );

        Assert.NotNull(controller);
    }
}
