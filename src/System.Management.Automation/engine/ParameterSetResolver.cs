// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation.Language;
using System.Numerics;

namespace System.Management.Automation;

/// <summary>
/// Encapsulates parameter-set state management, validation, and resolution.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class ParameterSetResolver
{
    private readonly CommandMetadata _commandMetadata;
    private readonly MergedCommandParameterMetadata _bindableParameters;
    private readonly IBindingStateContext _stateContext;
    private readonly IBindingOperationsContext _opsContext;

    internal ParameterSetResolver(
        CommandMetadata commandMetadata,
        MergedCommandParameterMetadata bindableParameters,
        IBindingStateContext stateContext,
        IBindingOperationsContext opsContext)
    {
        _commandMetadata = commandMetadata;
        _bindableParameters = bindableParameters;
        _stateContext = stateContext;
        _opsContext = opsContext;
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

    private string DebuggerDisplayValue
    {
        get
        {
            int validCount = ValidParameterSetCount(CurrentParameterSetFlag);
            bool resolved = HasSingleParameterSetSelected;
            return $"ParamSetResolver: 0x{CurrentParameterSetFlag:X8}, Valid={validCount}, Resolved={resolved}";
        }
    }

    /// <summary>Narrows the current parameter set flag by AND-ing with the given flags.</summary>
    internal void NarrowByParameterSetFlags(uint flags)
    {
        CurrentParameterSetFlag &= flags;
    }

    internal static ParameterSetResolver CreateDefault()
    {
        return new ParameterSetResolver(
            commandMetadata: new CommandMetadata(typeof(PSCmdlet)),
            bindableParameters: new MergedCommandParameterMetadata(),
            stateContext: s_defaultContext,
            opsContext: s_defaultContext);
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
                _opsContext.SetParameterSetName(currentParameterSetName);
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

            _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));
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
                    ParameterBinderBase.bindingTracer.WriteLine("{0} valid parameter sets, using the DEFAULT PARAMETER SET: [{0}]",
                        _bindableParameters.ParameterSetCount.ToString(),
                        _commandMetadata.DefaultParameterSetName);

                    CurrentParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;
                }
                else
                {
                    ParameterBinderBase.bindingTracer.TraceError("ERROR: {0} valid parameter sets, but NOT DEFAULT PARAMETER SET.", _bindableParameters.ParameterSetCount);

                    ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
                }
            }
        }
    }

    internal int ResolveParameterSetAmbiguityBasedOnMandatoryParameters()
    {
        uint currentParameterSetFlag = CurrentParameterSetFlag;
        int result = ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
            _stateContext.BoundParameters,
            _stateContext.UnboundParameters,
            _bindableParameters,
            ref currentParameterSetFlag,
            _opsContext.SetParameterSetName);

        CurrentParameterSetFlag = currentParameterSetFlag;

        return result;
    }

    internal static int ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
        Dictionary<string, MergedCompiledCommandParameter> boundParameters,
        ICollection<MergedCompiledCommandParameter> unboundParameters,
        MergedCommandParameterMetadata bindableParameters,
        ref uint currentParameterSetFlag,
        Action<string>? setParameterSetName)
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
            remainingParameterSetsWithNoMandatoryUnboundParameters &= ~parameterSetMetadata.ParameterSetFlag;
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
            ParameterBindingException.NewAmbiguousParameterSet(_stateContext.InvocationInfo);

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

        _opsContext.ThrowOrElaborateBindingException(bindingException);
    }

    internal static int ValidParameterSetCount(uint parameterSetFlags)
    {
        if (parameterSetFlags == uint.MaxValue)
        {
            return 1;
        }

        return BitOperations.PopCount(parameterSetFlags);
    }

    internal string CurrentParameterSetName => _bindableParameters.GetParameterSetName(CurrentParameterSetFlag);

    internal uint FilterParameterSetsTakingNoPipelineInput(ICollection<MergedCompiledCommandParameter> delayBindScriptBlockParameters)
    {
        uint parameterSetsTakingPipeInput = 0;
        bool findPipeParameterInAllSets = false;

        foreach (MergedCompiledCommandParameter parameter in delayBindScriptBlockParameters)
        {
            parameterSetsTakingPipeInput |= parameter.Parameter.ParameterSetFlags;
        }

        foreach (MergedCompiledCommandParameter parameter in _stateContext.UnboundParameters)
        {
            if (!parameter.Parameter.IsPipelineParameterInSomeParameterSet)
            {
                continue;
            }

            var matchingParameterSetMetadata =
                parameter.Parameter.GetMatchingParameterSetData(CurrentParameterSetFlag);

            foreach (ParameterSetSpecificMetadata parameterSetMetadata in matchingParameterSetMetadata)
            {
                if (parameterSetMetadata.ValueFromPipeline || parameterSetMetadata.ValueFromPipelineByPropertyName)
                {
                    if (parameterSetMetadata is { ParameterSetFlag: 0, IsInAllSets: true })
                    {
                        parameterSetsTakingPipeInput = 0;
                        findPipeParameterInAllSets = true;
                        break;
                    }

                    parameterSetsTakingPipeInput |= parameterSetMetadata.ParameterSetFlag;
                }
            }

            if (findPipeParameterInAllSets)
            {
                break;
            }
        }

        if (parameterSetsTakingPipeInput != 0)
        {
            return CurrentParameterSetFlag & parameterSetsTakingPipeInput;
        }

        return CurrentParameterSetFlag;
    }

    internal bool AtLeastOneUnboundValidParameterSetTakesPipelineInput(uint validParameterSetFlags)
    {
        foreach (MergedCompiledCommandParameter parameter in _stateContext.UnboundParameters)
        {
            if (parameter.Parameter.DoesParameterSetTakePipelineInput(validParameterSetFlags))
            {
                return true;
            }
        }

        return false;
    }

    internal List<MergedCompiledCommandParameter> GetMissingMandatoryParameters(int validParameterSetCount, bool isPipelineInputExpected)
    {
        List<MergedCompiledCommandParameter> result = new();

        uint defaultParameterSet = _commandMetadata.DefaultParameterSetFlag;
        uint commandMandatorySets = 0;

        Dictionary<uint, ParameterSetPromptingData> promptingData = CollectMandatoryPromptingData(
            defaultParameterSet,
            isPipelineInputExpected,
            result,
            ref commandMandatorySets,
            out bool missingAMandatoryParameter,
            out bool missingAMandatoryParameterInAllSet);

        if (missingAMandatoryParameter && isPipelineInputExpected)
        {
            if (commandMandatorySets == 0)
            {
                commandMandatorySets = CurrentParameterSetFlag;
            }

            if (missingAMandatoryParameterInAllSet)
            {
                uint availableParameterSetFlags = _bindableParameters.AllParameterSetFlags;
                if (availableParameterSetFlags == 0)
                {
                    availableParameterSetFlags = uint.MaxValue;
                }

                commandMandatorySets = CurrentParameterSetFlag & availableParameterSetFlags;
            }

            if (validParameterSetCount > 1 &&
                defaultParameterSet != 0 &&
                (defaultParameterSet & commandMandatorySets) == 0 &&
                (defaultParameterSet & CurrentParameterSetFlag) != 0)
            {
                uint setThatTakesPipelineInput = 0;
                foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
                {
                    if ((promptingSetData.ParameterSet & CurrentParameterSetFlag) != 0 &&
                        (promptingSetData.ParameterSet & defaultParameterSet) == 0 &&
                        !promptingSetData.IsAllSet)
                    {
                        if (promptingSetData.PipelineableMandatoryParameters.Count > 0)
                        {
                            setThatTakesPipelineInput = promptingSetData.ParameterSet;
                            break;
                        }
                    }
                }

                if (setThatTakesPipelineInput == 0)
                {
                    commandMandatorySets = CurrentParameterSetFlag & (~commandMandatorySets);
                    CurrentParameterSetFlag = commandMandatorySets;

                    if (CurrentParameterSetFlag == defaultParameterSet)
                    {
                        _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));
                    }
                    else
                    {
                        ParameterSetToBePrioritizedInPipelineBinding = defaultParameterSet;
                    }
                }
            }

            int commandMandatorySetsCount = ValidParameterSetCount(commandMandatorySets);
            if (commandMandatorySetsCount == 0)
            {
                ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
            }
            else if (commandMandatorySetsCount == 1)
            {
                CollectNonpipelineableMandatoryParameters(promptingData, commandMandatorySets, result);
            }
            else if (ParameterSetToBePrioritizedInPipelineBinding == 0)
            {
                if (!TryLatchToDefaultParameterSet(promptingData, commandMandatorySets, defaultParameterSet, result))
                {
                    TryLatchToUniquePipelineSet(promptingData, commandMandatorySets, result);
                }
            }
        }

        return result;
    }

    private Dictionary<uint, ParameterSetPromptingData> CollectMandatoryPromptingData(
        uint defaultParameterSet,
        bool isPipelineInputExpected,
        List<MergedCompiledCommandParameter> result,
        ref uint commandMandatorySets,
        out bool missingAMandatoryParameter,
        out bool missingAMandatoryParameterInAllSet)
    {
        Dictionary<uint, ParameterSetPromptingData> promptingData = new Dictionary<uint, ParameterSetPromptingData>();

        missingAMandatoryParameter = false;
        missingAMandatoryParameterInAllSet = false;

        foreach (MergedCompiledCommandParameter parameter in _stateContext.UnboundParameters)
        {
            if (!parameter.Parameter.IsMandatoryInSomeParameterSet)
            {
                continue;
            }

            var matchingParameterSetMetadata = parameter.Parameter.GetMatchingParameterSetData(CurrentParameterSetFlag);

            uint parameterMandatorySets = 0;
            bool thisParameterMissing = false;

            foreach (ParameterSetSpecificMetadata parameterSetMetadata in matchingParameterSetMetadata)
            {
                uint newMandatoryParameterSetFlag = NewParameterSetPromptingData(promptingData, parameter, parameterSetMetadata, defaultParameterSet, isPipelineInputExpected);

                if (newMandatoryParameterSetFlag == 0)
                {
                    continue;
                }

                missingAMandatoryParameter = true;
                thisParameterMissing = true;

                if (newMandatoryParameterSetFlag != uint.MaxValue)
                {
                    parameterMandatorySets |= (CurrentParameterSetFlag & newMandatoryParameterSetFlag);
                    commandMandatorySets |= (CurrentParameterSetFlag & parameterMandatorySets);
                }
                else
                {
                    missingAMandatoryParameterInAllSet = true;
                }
            }

            if (!isPipelineInputExpected && thisParameterMissing)
            {
                result.Add(parameter);
            }
        }

        return promptingData;
    }

    internal static void CollectNonpipelineableMandatoryParameters(
        Dictionary<uint, ParameterSetPromptingData> promptingData,
        uint mandatorySets,
        List<MergedCompiledCommandParameter> result)
    {
        foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
        {
            if ((promptingSetData.ParameterSet & mandatorySets) == 0 && !promptingSetData.IsAllSet)
            {
                continue;
            }

            foreach (MergedCompiledCommandParameter mandatoryParameter in promptingSetData.NonpipelineableMandatoryParameters.Keys)
            {
                result.Add(mandatoryParameter);
            }
        }
    }

    private bool TryLatchToDefaultParameterSet(
        Dictionary<uint, ParameterSetPromptingData> promptingData,
        uint commandMandatorySets,
        uint defaultParameterSet,
        List<MergedCompiledCommandParameter> result)
    {
        if (defaultParameterSet == 0 || (commandMandatorySets & defaultParameterSet) == 0)
        {
            return false;
        }

        bool anotherSetTakesPipelineInput = false;
        foreach (ParameterSetPromptingData paramPromptingData in promptingData.Values)
        {
            if (paramPromptingData is { IsAllSet: false, IsDefaultSet: false } &&
                paramPromptingData.PipelineableMandatoryParameters.Count > 0 &&
                paramPromptingData.NonpipelineableMandatoryParameters.Count == 0)
            {
                anotherSetTakesPipelineInput = true;
                break;
            }
        }

        bool anotherSetTakesPipelineInputByPropertyName = false;
        foreach (ParameterSetPromptingData paramPromptingData in promptingData.Values)
        {
            if (!paramPromptingData.IsAllSet &&
                !paramPromptingData.IsDefaultSet &&
                paramPromptingData.PipelineableMandatoryByPropertyNameParameters.Count > 0)
            {
                anotherSetTakesPipelineInputByPropertyName = true;
                break;
            }
        }

        bool latchOnToDefault = false;
        if (promptingData.TryGetValue(defaultParameterSet, out ParameterSetPromptingData? defaultSetPromptingData))
        {
            bool defaultSetTakesPipelineInput = defaultSetPromptingData.PipelineableMandatoryParameters.Count > 0;
            bool defaultSetTakesPipelineInputByPropertyName = defaultSetPromptingData.PipelineableMandatoryByPropertyNameParameters.Count > 0;

            if (defaultSetTakesPipelineInputByPropertyName && !anotherSetTakesPipelineInputByPropertyName)
            {
                latchOnToDefault = true;
            }
            else if (defaultSetTakesPipelineInput && !anotherSetTakesPipelineInput)
            {
                latchOnToDefault = true;
            }
        }

        if (!latchOnToDefault && !anotherSetTakesPipelineInput)
        {
            latchOnToDefault = true;
        }

        if (!latchOnToDefault && promptingData.TryGetValue(uint.MaxValue, out ParameterSetPromptingData? allSetPromptingData))
        {
            latchOnToDefault = allSetPromptingData.NonpipelineableMandatoryParameters.Count > 0;
        }

        if (!latchOnToDefault)
        {
            return false;
        }

        CurrentParameterSetFlag = defaultParameterSet;
        _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));

        CollectNonpipelineableMandatoryParameters(promptingData, defaultParameterSet, result);
        return true;
    }

    private bool TryLatchToUniquePipelineSet(
        Dictionary<uint, ParameterSetPromptingData> promptingData,
        uint commandMandatorySets,
        List<MergedCompiledCommandParameter> result)
    {
        uint setThatTakesPipelineInputByValue = 0;
        uint setThatTakesPipelineInputByPropertyName = 0;

        bool foundSetThatTakesPipelineInputByValue = false;
        bool foundMultipleSetsThatTakesPipelineInputByValue = false;
        foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
        {
            if ((promptingSetData.ParameterSet & commandMandatorySets) != 0 &&
                !promptingSetData.IsAllSet)
            {
                if (promptingSetData.PipelineableMandatoryByValueParameters.Count > 0)
                {
                    if (foundSetThatTakesPipelineInputByValue)
                    {
                        foundMultipleSetsThatTakesPipelineInputByValue = true;
                        setThatTakesPipelineInputByValue = 0;
                        break;
                    }

                    setThatTakesPipelineInputByValue = promptingSetData.ParameterSet;
                    foundSetThatTakesPipelineInputByValue = true;
                }
            }
        }

        bool foundSetThatTakesPipelineInputByPropertyName = false;
        bool foundMultipleSetsThatTakesPipelineInputByPropertyName = false;
        foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
        {
            if ((promptingSetData.ParameterSet & commandMandatorySets) != 0 &&
                    !promptingSetData.IsAllSet)
            {
                if (promptingSetData.PipelineableMandatoryByPropertyNameParameters.Count > 0)
                {
                    if (foundSetThatTakesPipelineInputByPropertyName)
                    {
                        foundMultipleSetsThatTakesPipelineInputByPropertyName = true;
                        setThatTakesPipelineInputByPropertyName = 0;
                        break;
                    }

                    setThatTakesPipelineInputByPropertyName = promptingSetData.ParameterSet;
                    foundSetThatTakesPipelineInputByPropertyName = true;
                }
            }
        }

        uint uniqueSetThatTakesPipelineInput = 0;
        if (foundSetThatTakesPipelineInputByValue && foundSetThatTakesPipelineInputByPropertyName &&
            (setThatTakesPipelineInputByValue == setThatTakesPipelineInputByPropertyName))
        {
            uniqueSetThatTakesPipelineInput = setThatTakesPipelineInputByValue;
        }

        if (foundSetThatTakesPipelineInputByValue ^ foundSetThatTakesPipelineInputByPropertyName)
        {
            uniqueSetThatTakesPipelineInput = foundSetThatTakesPipelineInputByValue ?
                setThatTakesPipelineInputByValue : setThatTakesPipelineInputByPropertyName;
        }

        if (uniqueSetThatTakesPipelineInput != 0)
        {
            uint otherMandatorySetsToBeIgnored = 0;
            bool chosenMandatorySetContainsNonpipelineableMandatoryParameters = false;

            foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
            {
                if ((promptingSetData.ParameterSet & uniqueSetThatTakesPipelineInput) != 0 ||
                    promptingSetData.IsAllSet)
                {
                    if (!promptingSetData.IsAllSet)
                    {
                        chosenMandatorySetContainsNonpipelineableMandatoryParameters =
                            promptingSetData.NonpipelineableMandatoryParameters.Count > 0;
                    }
                }
                else
                {
                    otherMandatorySetsToBeIgnored |= promptingSetData.ParameterSet;
                }
            }

            CollectNonpipelineableMandatoryParameters(promptingData, uniqueSetThatTakesPipelineInput, result);

            PreservePotentialParameterSets(uniqueSetThatTakesPipelineInput,
                                           otherMandatorySetsToBeIgnored,
                                           chosenMandatorySetContainsNonpipelineableMandatoryParameters);

            return true;
        }

        bool foundMissingParameters = false;
        uint setsThatContainNonpipelineableMandatoryParameter = 0;
        foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
        {
            if ((promptingSetData.ParameterSet & commandMandatorySets) != 0 ||
                 promptingSetData.IsAllSet)
            {
                if (promptingSetData.NonpipelineableMandatoryParameters.Count > 0)
                {
                    foundMissingParameters = true;
                    if (!promptingSetData.IsAllSet)
                    {
                        setsThatContainNonpipelineableMandatoryParameter |= promptingSetData.ParameterSet;
                    }
                }
            }
        }

        if (!foundMissingParameters)
        {
            return false;
        }

        if (setThatTakesPipelineInputByValue != 0)
        {
            uint otherMandatorySetsToBeIgnored = 0;
            bool chosenMandatorySetContainsNonpipelineableMandatoryParameters = false;

            foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
            {
                if ((promptingSetData.ParameterSet & setThatTakesPipelineInputByValue) != 0 ||
                    promptingSetData.IsAllSet)
                {
                    if (!promptingSetData.IsAllSet)
                    {
                        chosenMandatorySetContainsNonpipelineableMandatoryParameters =
                            promptingSetData.NonpipelineableMandatoryParameters.Count > 0;
                    }
                }
                else
                {
                    otherMandatorySetsToBeIgnored |= promptingSetData.ParameterSet;
                }
            }

            CollectNonpipelineableMandatoryParameters(promptingData, setThatTakesPipelineInputByValue, result);

            PreservePotentialParameterSets(setThatTakesPipelineInputByValue,
                                           otherMandatorySetsToBeIgnored,
                                           chosenMandatorySetContainsNonpipelineableMandatoryParameters);
            return false;
        }

        if ((!foundMultipleSetsThatTakesPipelineInputByValue) &&
           (!foundMultipleSetsThatTakesPipelineInputByPropertyName))
        {
            ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
        }

        if (setsThatContainNonpipelineableMandatoryParameter != 0)
        {
            IgnoreOtherMandatoryParameterSets(setsThatContainNonpipelineableMandatoryParameter);
            if (CurrentParameterSetFlag == 0)
            {
                ThrowAmbiguousParameterSetException(CurrentParameterSetFlag);
            }

            if (ValidParameterSetCount(CurrentParameterSetFlag) == 1)
            {
                _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));
            }
        }

        return false;
    }

    private void PreservePotentialParameterSets(uint chosenMandatorySet, uint otherMandatorySetsToBeIgnored, bool chosenSetContainsNonpipelineableMandatoryParameters)
    {
        if (chosenSetContainsNonpipelineableMandatoryParameters)
        {
            CurrentParameterSetFlag = chosenMandatorySet;
            _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));
        }
        else
        {
            IgnoreOtherMandatoryParameterSets(otherMandatorySetsToBeIgnored);
            _opsContext.SetParameterSetName(_bindableParameters.GetParameterSetName(CurrentParameterSetFlag));

            if (CurrentParameterSetFlag != chosenMandatorySet)
            {
                ParameterSetToBePrioritizedInPipelineBinding = chosenMandatorySet;
            }
        }
    }

    private void IgnoreOtherMandatoryParameterSets(uint otherMandatorySetsToBeIgnored)
    {
        if (otherMandatorySetsToBeIgnored == 0)
        {
            return;
        }

        if (CurrentParameterSetFlag == uint.MaxValue)
        {
            uint availableParameterSets = _bindableParameters.AllParameterSetFlags;
            Diagnostics.Assert(availableParameterSets != 0, "At least one parameter set must be declared");
            CurrentParameterSetFlag = availableParameterSets & (~otherMandatorySetsToBeIgnored);
        }
        else
        {
            CurrentParameterSetFlag &= (~otherMandatorySetsToBeIgnored);
        }
    }

    internal static uint NewParameterSetPromptingData(
        Dictionary<uint, ParameterSetPromptingData> promptingData,
        MergedCompiledCommandParameter parameter,
        ParameterSetSpecificMetadata parameterSetMetadata,
        uint defaultParameterSet,
        bool pipelineInputExpected)
    {
        uint parameterMandatorySets = 0;
        uint parameterSetFlag = parameterSetMetadata.ParameterSetFlag;
        if (parameterSetFlag == 0)
        {
            parameterSetFlag = uint.MaxValue;
        }

        bool isDefaultSet = (defaultParameterSet != 0) && ((defaultParameterSet & parameterSetFlag) != 0);

        bool isMandatory = false;
        if (parameterSetMetadata.IsMandatory)
        {
            parameterMandatorySets |= parameterSetFlag;
            isMandatory = true;
        }

        bool isPipelineable = false;
        if (pipelineInputExpected)
        {
            if (parameterSetMetadata.ValueFromPipeline || parameterSetMetadata.ValueFromPipelineByPropertyName)
            {
                isPipelineable = true;
            }
        }

        if (isMandatory)
        {
            if (!promptingData.TryGetValue(parameterSetFlag, out ParameterSetPromptingData? promptingDataForSet))
            {
                promptingDataForSet = new ParameterSetPromptingData(parameterSetFlag, isDefaultSet);
                promptingData.Add(parameterSetFlag, promptingDataForSet);
            }

            if (isPipelineable)
            {
                promptingDataForSet.PipelineableMandatoryParameters[parameter] = parameterSetMetadata;

                if (parameterSetMetadata.ValueFromPipeline)
                {
                    promptingDataForSet.PipelineableMandatoryByValueParameters[parameter] = parameterSetMetadata;
                }

                if (parameterSetMetadata.ValueFromPipelineByPropertyName)
                {
                    promptingDataForSet.PipelineableMandatoryByPropertyNameParameters[parameter] = parameterSetMetadata;
                }
            }
            else
            {
                promptingDataForSet.NonpipelineableMandatoryParameters[parameter] = parameterSetMetadata;
            }
        }

        return parameterMandatorySets;
    }

    private static readonly DefaultBindingContext s_defaultContext = new DefaultBindingContext();

    private sealed class DefaultBindingContext : IBindingStateContext, IBindingOperationsContext
    {
        // IBindingStateContext — only UnboundParameters/BoundParameters/InvocationInfo are actually called

        public IList<MergedCompiledCommandParameter> UnboundParameters { get; } = new List<MergedCompiledCommandParameter>();

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; } = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandParameterInternal> BoundArguments { get; } = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        public List<CommandParameterInternal> UnboundArguments { get; set; } = new List<CommandParameterInternal>();

        public List<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; } = new List<MergedCompiledCommandParameter>();

        public InvocationInfo InvocationInfo { get; } = new InvocationInfo(null, null);

        public ParameterSetResolver ParameterSetResolver { get; } = null!;

        public uint CurrentParameterSetFlag { get; } = 0;

        public uint DefaultParameterSetFlag { get; set; } = 0;

        public string DefaultParameterSetName { get; } = string.Empty;

        public string CommandName { get; } = string.Empty;

        public bool ImplementsDynamicParameters { get; } = false;

        public Cmdlet Command { get; } = null!;

        public ExecutionContext Context { get; } = null!;

        public MergedCommandParameterMetadata BindableParameters { get; } = null!;

        public CommandLineParameters CommandLineParameters { get; } = null!;

        public bool DefaultParameterBindingInUse { get; set; } = false;

        public List<string> BoundDefaultParameters { get; } = new List<string>();

        public List<string>? DefaultParameterAliasList { get; set; } = null;

        public HashSet<string> DefaultParameterWarningSet { get; } = new HashSet<string>();

        public Dictionary<MergedCompiledCommandParameter, object>? AllDefaultParameterValuePairs { get; set; } = null;

        public bool UseDefaultParameterBinding { get; set; } = false;

        public Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; } = new Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument>();

        public Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; } = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        // IBindingOperationsContext — only SetParameterSetName/ThrowOrElaborateBindingException actually called

        public void SetParameterSetName(string parameterSetName) { }

        public void ThrowOrElaborateBindingException(ParameterBindingException exception) => throw exception;

        public bool DispatchBindToSubBinder(uint validParameterSetFlag, CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags) => false;

        public bool BindToAssociatedBinder(CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags) => false;

        public bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags) => false;

        public void ReparseUnboundArguments() { }

        public void BindNamedParameters(uint parameterSetFlag, List<CommandParameterInternal> args) { }

        public void BindPositionalParameters(List<CommandParameterInternal> args, uint currentParameterSetFlag, uint defaultParameterSetFlag, out ParameterBindingException? outgoingBindingException) { outgoingBindingException = null; }

        public IScriptExtent GetErrorExtent(CommandParameterInternal argument) => null!;

        public object? GetDefaultParameterValue(string name) => null;

        public bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter) => false;

        public HashSet<string> CopyBoundPositionalParameters() => new HashSet<string>();

        public bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind) { thereWasSomethingToBind = false; return false; }

        public void BackupDefaultParameter(MergedCompiledCommandParameter parameter) { }

        public void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters) { }

        public CommandParameterInternal RentPipelineCpi() => null!;

        public void ReturnPipelineCpi(CommandParameterInternal cpi) { }

        public bool ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag) => false;
    }
}
