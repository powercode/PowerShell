// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using System.Management.Automation.Internal.Host;

namespace PSTests.Sequential;

[Cmdlet(VerbsLifecycle.Invoke, "ScopedNestedPrompt")]
public sealed class ScopedNestedPromptCommand : PSCmdlet
{
    protected override void ProcessRecord() => ((InternalHost)Host).EnterNestedPrompt(this);
}
