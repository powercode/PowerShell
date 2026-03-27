// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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

    /// <summary>
    /// Ensures that only one parameter set is valid or throws an appropriate exception.
    /// </summary>
    internal int ValidateParameterSets(bool prePipelineInput, bool setDefault, Func<uint, bool> atLeastOneUnboundValidParameterSetTakesPipelineInput)
    {
        int validParameterSetCount = ValidParameterSetCount(CurrentParameterSetFlag);

        if (validParameterSetCount == 0 && CurrentParameterSetFlag != uint.MaxValue)
        {
            ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
        }
        else if (validParameterSetCount > 1)
        {
            uint defaultParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;
            bool hasDefaultSetDefined = defaultParameterSetFlag != 0;

            bool validSetIsAllSet = CurrentParameterSetFlag == uint.MaxValue;
            bool validSetIsDefault = CurrentParameterSetFlag == defaultParameterSetFlag;

            if (validSetIsAllSet && !hasDefaultSetDefined)
            {
                validParameterSetCount = 1;
            }
            else if (!prePipelineInput &&
                validSetIsDefault ||
                (hasDefaultSetDefined && (CurrentParameterSetFlag & defaultParameterSetFlag) != 0))
            {
                string currentParameterSetName = _bindableParameters.GetParameterSetName(defaultParameterSetFlag);
                _context.SetParameterSetName(currentParameterSetName);
                if (setDefault)
                {
                    CurrentParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;
                    validParameterSetCount = 1;
                }
            }
            else if (prePipelineInput &&
                atLeastOneUnboundValidParameterSetTakesPipelineInput(CurrentParameterSetFlag))
            {
                // Pipeline input may still disambiguate among valid sets.
            }
            else
            {
                int resolvedParameterSetCount = ResolveParameterSetAmbiguityBasedOnMandatoryParameters();
                if (resolvedParameterSetCount != 1)
                {
                    ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
                }

                validParameterSetCount = resolvedParameterSetCount;
            }
        }
        else
        {
            if (!HasSingleParameterSetSelected)
            {
                validParameterSetCount =
                    (_bindableParameters.ParameterSetCount > 0) ?
                        _bindableParameters.ParameterSetCount : 1;

                if (prePipelineInput &&
                    atLeastOneUnboundValidParameterSetTakesPipelineInput(CurrentParameterSetFlag))
                {
                    // Don't fixate on the default parameter set yet.
                }
                else if (_commandMetadata.DefaultParameterSetFlag != 0)
                {
                    if (setDefault)
                    {
                        CurrentParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;
                        validParameterSetCount = 1;
                    }
                }
                else if (validParameterSetCount > 1)
                {
                    int resolvedParameterSetCount = ResolveParameterSetAmbiguityBasedOnMandatoryParameters();
                    if (resolvedParameterSetCount != 1)
                    {
                        ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
                    }

                    validParameterSetCount = resolvedParameterSetCount;
                }
            }

            _context.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));
        }

        return validParameterSetCount;
    }

    internal void VerifyParameterSetSelected()
    {
        if (_bindableParameters.ParameterSetCount > 1)
        {
            if (CurrentParameterSetFlag == uint.MaxValue)
            {
                if ((CurrentParameterSetFlag & _commandMetadata.DefaultParameterSetFlag) != 0 &&
                     _commandMetadata.DefaultParameterSetFlag != uint.MaxValue)
                {
                    ParameterBinderBase.bindingTracer.WriteLine(
                        "{0} valid parameter sets, using the DEFAULT PARAMETER SET: [{0}]",
                        _bindableParameters.ParameterSetCount.ToString(),
                        _commandMetadata.DefaultParameterSetName);

                    CurrentParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;
                }
                else
                {
                    ParameterBinderBase.bindingTracer.TraceError(
                        "ERROR: {0} valid parameter sets, but NOT DEFAULT PARAMETER SET.",
                        _bindableParameters.ParameterSetCount);

                    ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
                }
            }
        }
    }

    internal int ResolveParameterSetAmbiguityBasedOnMandatoryParameters()
    {
        uint currentParameterSetFlag = CurrentParameterSetFlag;
        int result = ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
            _context.BoundParameters,
            _context.UnboundParameters,
            _bindableParameters,
            ref currentParameterSetFlag,
            _context.SetParameterSetName);

        CurrentParameterSetFlag = currentParameterSetFlag;

        return result;
    }

    internal static int ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
        Dictionary<string, MergedCompiledCommandParameter> boundParameters,
        ICollection<MergedCompiledCommandParameter> unboundParameters,
        MergedCommandParameterMetadata bindableParameters,
        ref uint currentParameterSetFlag,
        Action<string> setParameterSetName)
    {
        uint remainingParameterSetsWithNoMandatoryUnboundParameters = currentParameterSetFlag;

        IEnumerable<ParameterSetSpecificMetadata> allParameterSetMetadatas = boundParameters.Values
            .Concat(unboundParameters)
            .SelectMany(static p => p.Parameter.ParameterSetData.Values);
        uint allParameterSetFlags = 0;
        foreach (ParameterSetSpecificMetadata parameterSetMetadata in allParameterSetMetadatas)
        {
            allParameterSetFlags |= parameterSetMetadata.ParameterSetFlag;
        }

        remainingParameterSetsWithNoMandatoryUnboundParameters &= allParameterSetFlags;

        Diagnostics.Assert(
            ValidParameterSetCount(remainingParameterSetsWithNoMandatoryUnboundParameters) > 1,
            "This method should only be called when there is an ambiguity wrt parameter sets");

        IEnumerable<ParameterSetSpecificMetadata> parameterSetMetadatasForUnboundMandatoryParameters = unboundParameters
            .SelectMany(static p => p.Parameter.ParameterSetData.Values)
            .Where(static p => p.IsMandatory);
        foreach (ParameterSetSpecificMetadata parameterSetMetadata in parameterSetMetadatasForUnboundMandatoryParameters)
        {
            remainingParameterSetsWithNoMandatoryUnboundParameters &= (~parameterSetMetadata.ParameterSetFlag);
        }

        int finalParameterSetCount = ValidParameterSetCount(remainingParameterSetsWithNoMandatoryUnboundParameters);
        if (finalParameterSetCount == 1)
        {
            currentParameterSetFlag = remainingParameterSetsWithNoMandatoryUnboundParameters;

            if (setParameterSetName is not null)
            {
                string currentParameterSetName = bindableParameters.GetParameterSetName(currentParameterSetFlag);
                setParameterSetName(currentParameterSetName);
            }

            return finalParameterSetCount;
        }

        return -1;
    }

    internal void ThrowAmbiguousParameterSetException(uint parameterSetFlags)
    {
        ParameterBindingException bindingException =
            ParameterBindingException.NewAmbiguousParameterSet(_context.InvocationInfo);

        uint currentParameterSet = 1;

        while (parameterSetFlags != 0)
        {
            uint currentParameterSetActive = parameterSetFlags & 0x1;

            if (currentParameterSetActive == 1)
            {
                string parameterSetName = _bindableParameters.GetParameterSetName(currentParameterSet);
                if (!string.IsNullOrEmpty(parameterSetName))
                {
                    ParameterBinderBase.bindingTracer.WriteLine("Remaining valid parameter set: {0}", parameterSetName);
                }
            }

            parameterSetFlags >>= 1;
            currentParameterSet <<= 1;
        }

        _context.ThrowBindingException(bindingException);
    }

    internal static int ValidParameterSetCount(uint parameterSetFlags)
    {
        if (parameterSetFlags == uint.MaxValue)
        {
            return 1;
        }

        return BitOperations.PopCount(parameterSetFlags);
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
