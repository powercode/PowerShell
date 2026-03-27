// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace System.Management.Automation;

/// <summary>
/// Provides the live binding state that <see cref="ParameterSetResolver"/> needs
/// from its owning parameter binder controller.
/// </summary>
internal interface IParameterBindingContext
{
    /// <summary>Parameters not yet bound to a value.</summary>
    ICollection<MergedCompiledCommandParameter> UnboundParameters { get; }

    /// <summary>Parameters already bound to a value, keyed by name.</summary>
    Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; }

    /// <summary>Sets the resolved parameter set name on the command.</summary>
    void SetParameterSetName(string parameterSetName);

    /// <summary>Throws or elaborates a parameter binding exception.</summary>
    void ThrowBindingException(ParameterBindingException exception);

    /// <summary>The invocation info for trace and exception context.</summary>
    InvocationInfo InvocationInfo { get; }
}

/// <summary>
/// Encapsulates parameter-set state management, validation, and resolution.
/// Extracted from CmdletParameterBinderController to satisfy SRP.
/// </summary>
internal sealed class ParameterSetResolver
{
    private readonly CommandMetadata _commandMetadata;
    private readonly MergedCommandParameterMetadata _bindableParameters;
    private readonly IParameterBindingContext _context;

    internal ParameterSetResolver(
        CommandMetadata commandMetadata,
        MergedCommandParameterMetadata bindableParameters,
        IParameterBindingContext context)
    {
        _commandMetadata = commandMetadata;
        _bindableParameters = bindableParameters;
        _context = context;
    }

    /// <summary>Current valid parameter set flag (bit-mask). uint.MaxValue = all sets valid.</summary>
    internal uint CurrentParameterSetFlag { get; set; } = uint.MaxValue;

    /// <summary>Snapshot taken before pipeline processing to allow resetting per-object.</summary>
    internal uint PrePipelineProcessingParameterSetFlags { get; set; } = uint.MaxValue;

    /// <summary>Set to prioritize during pipeline binding when mandatory analysis identifies a preferred set.</summary>
    internal uint ParameterSetToBePrioritizedInPipelineBinding { get; set; }

    /// <summary>True when exactly one parameter set bit is selected (not AllSet, not zero).</summary>
    internal bool HasSingleParameterSetSelected =>
        CurrentParameterSetFlag != 0 &&
        CurrentParameterSetFlag != uint.MaxValue &&
        (CurrentParameterSetFlag & (CurrentParameterSetFlag - 1)) == 0;

    /// <summary>Narrows the current parameter set flag by AND-ing with the given flags.</summary>
    internal void NarrowByParameterSetFlags(uint flags)
    {
        if (flags != 0)
        {
            CurrentParameterSetFlag &= flags;
        }
    }

    internal static ParameterSetResolver CreateDefault()
    {
        return new ParameterSetResolver(
            commandMetadata: new CommandMetadata(typeof(PSCmdlet)),
            bindableParameters: new MergedCommandParameterMetadata(),
            context: s_defaultContext);
    }

    private static readonly IParameterBindingContext s_defaultContext = new DefaultBindingContext();

    private sealed class DefaultBindingContext : IParameterBindingContext
    {
        public ICollection<MergedCompiledCommandParameter> UnboundParameters { get; } = new List<MergedCompiledCommandParameter>();

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; } = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        public InvocationInfo InvocationInfo { get; } = new InvocationInfo(null, null);

        public void SetParameterSetName(string parameterSetName)
        {
        }

        public void ThrowBindingException(ParameterBindingException exception)
        {
            throw exception;
        }
    }
}
