// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Text;

namespace System.Management.Automation;

/// <summary>
/// Provides the callback that <see cref="MandatoryParameterPrompter"/> needs
/// from its owning parameter binder controller to bind prompted values.
/// </summary>
internal interface IMandatoryParameterPrompterContext
{
    /// <summary>Resolves and binds a named parameter after prompting.</summary>
    bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags);
}

/// <summary>
/// Encapsulates mandatory-parameter detection, user prompting, and prompt data structure building.
/// Extracted from <see cref="CmdletParameterBinderController"/> to satisfy SRP.
/// </summary>
internal sealed class MandatoryParameterPrompter
{
    [TraceSource("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).")]
    private static readonly PSTraceSource s_tracer =
        PSTraceSource.GetTracer(
            "ParameterBinderController",
            "Controls the interaction between the command processor and the parameter binder(s).");

    [TraceSource("ParameterBinding", "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.")]
    internal static readonly PSTraceSource bindingTracer =
        PSTraceSource.GetTracer(
            "ParameterBinding",
            "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.",
            false);

    private readonly ParameterSetResolver _parameterSetResolver;
    private readonly ExecutionContext _context;
    private readonly Cmdlet _command;
    private readonly IMandatoryParameterPrompterContext _bindingContext;

    internal MandatoryParameterPrompter(
        ParameterSetResolver parameterSetResolver,
        ExecutionContext context,
        Cmdlet command,
        IMandatoryParameterPrompterContext bindingContext)
    {
        _parameterSetResolver = parameterSetResolver;
        _context = context;
        _command = command;
        _bindingContext = bindingContext;
    }
}
