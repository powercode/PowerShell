// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;

namespace PSTests.Sequential
{
    [Cmdlet(VerbsCommon.Get, "ScopedRemotingProbe")]
    public sealed class ScopedRemotingProbeCmdlet : Microsoft.PowerShell.Commands.PSRemotingCmdlet
    {
        protected override void BeginProcessing()
        {
            // This probe exercises preference resolution without starting a remoting transport.
        }

        protected override void ProcessRecord()
        {
            WriteObject(string.Join(
                "|",
                ResolveShell(null),
                ResolveAppName(null),
                ResolveShell("ExplicitShell"),
                ResolveAppName("ExplicitApp")));
        }
    }
}
