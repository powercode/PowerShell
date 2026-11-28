// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;

namespace PSTests.Sequential;

[Cmdlet(VerbsLifecycle.Invoke, "ScopedQuestionMarkWriteError")]
public sealed class ScopedQuestionMarkWriteErrorCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteError(new ErrorRecord(
            new InvalidOperationException("expected question-mark probe error"),
            "ScopedQuestionMarkProbeError",
            ErrorCategory.NotSpecified,
            null));
    }
}
