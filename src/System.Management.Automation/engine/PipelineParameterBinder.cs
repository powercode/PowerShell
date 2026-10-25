// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;

namespace System.Management.Automation;

/// <summary>
/// Provides the binding context that <see cref="PipelineParameterBinder"/> needs
/// from its owning parameter binder controller.
/// </summary>
internal interface IPipelineParameterBindingContext
{
    /// <summary>Parameters not yet bound to a value.</summary>
    IList<MergedCompiledCommandParameter> UnboundParameters { get; }

    /// <summary>Parameters bound through pipeline input in the current pipeline iteration.</summary>
    Collection<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; }

    /// <summary>The parameter-set resolver for the current command.</summary>
    ParameterSetResolver ParameterSetResolver { get; }

    /// <summary>Whether $PSDefaultParameterValues binding is currently in use.</summary>
    bool DefaultParameterBindingInUse { get; set; }

    /// <summary>The default parameter set flag from the command metadata.</summary>
    uint DefaultParameterSetFlag { get; }

    /// <summary>The name of the command being bound (for tracing).</summary>
    string CommandName { get; }

    /// <summary>
    /// Invokes any delay-bind ScriptBlocks and binds the evaluated result.
    /// Will be delegated to DelayBindScriptBlockHandler after Task D3.
    /// </summary>
    bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind);

    /// <summary>
    /// Backs up the default value of <paramref name="parameter"/> before pipeline binding overwrites it.
    /// Will be delegated to DefaultValueManager after Task D5.
    /// </summary>
    void BackupDefaultParameter(MergedCompiledCommandParameter parameter);

    /// <summary>
    /// Restores the default values of <paramref name="parameters"/> that were overwritten during pipeline binding.
    /// Will be delegated to DefaultValueManager after Task D5.
    /// </summary>
    void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters);

    /// <summary>Rents a <see cref="CommandParameterInternal"/> from the pipeline CPI pool.</summary>
    CommandParameterInternal RentPipelineCpi();

    /// <summary>Dispatches a parameter bind call to the appropriate sub-binder.</summary>
    bool DispatchBindToSubBinder(
        uint validParameterSetFlag,
        CommandParameterInternal argument,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags);

    /// <summary>Throws or elaborates a parameter binding exception.</summary>
    [DoesNotReturn]
    void ThrowOrElaborateBindingException(ParameterBindingException ex);

    /// <summary>
    /// Attempts to apply $PSDefaultParameterValues bindings for any yet-unbound parameters.
    /// </summary>
    bool ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag);
}

/// <summary>
/// Encapsulates the pipeline-input parameter binding state-machine.
/// Extracted from <see cref="CmdletParameterBinderController"/> to satisfy SRP.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class PipelineParameterBinder
{
    [TraceSource("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).")]
    private static readonly PSTraceSource s_tracer =
        PSTraceSource.GetTracer(
            "ParameterBinderController",
            "Controls the interaction between the command processor and the parameter binder(s).");

    private readonly IPipelineParameterBindingContext _context;

    /// <summary>Cached pipeline binding plan established after the first successful pipeline bind.</summary>
    private PipelineBindingPlan? _cachedPlan;

    internal PipelineParameterBinder(IPipelineParameterBindingContext context)
    {
        _context = context;
    }

    /// <summary>Clears the cached pipeline binding plan.</summary>
    internal void ResetPipelinePlan()
    {
        _cachedPlan = null;
    }

    private string DebuggerDisplayValue
        => $"PipelineParameterBinder: UnboundCount={_context.UnboundParameters.Count}";

    /// <summary>Used for defining the state of the binding state machine.</summary>
    private enum CurrentlyBinding
    {
        ValueFromPipelineNoCoercion = 0,
        ValueFromPipelineByPropertyNameNoCoercion = 1,
        ValueFromPipelineWithCoercion = 2,
        ValueFromPipelineByPropertyNameWithCoercion = 3
    }

    /// <summary>
    /// Binds the specified object or its properties to parameters
    /// that accept pipeline input.
    /// </summary>
    /// <param name="inputToOperateOn">
    /// The pipeline object to bind.
    /// </param>
    /// <returns>
    /// True if the pipeline input was bound successfully or there was nothing
    /// to bind, or false if there was an error.
    /// </returns>
    internal bool BindPipelineParameters(PSObject inputToOperateOn)
    {
        bool result;

        try
        {
            using (ParameterBinderBase.bindingTracer.TraceScope(
                "BIND PIPELINE object to parameters: [{0}]",
                _context.CommandName))
            {
                // First run any of the delay bind ScriptBlocks and bind the
                // result to the appropriate parameter.

                bool thereWasSomethingToBind;
                bool invokeScriptResult = _context.InvokeAndBindDelayBindScriptBlock(inputToOperateOn, out thereWasSomethingToBind);

                bool continueBindingAfterScriptBlockProcessing = !thereWasSomethingToBind || invokeScriptResult;

                bool bindPipelineParametersResult = false;

                if (continueBindingAfterScriptBlockProcessing)
                {
                    // If any of the parameters in the parameter set which are not yet bound
                    // accept pipeline input, process the input object and bind to those
                    // parameters

                    bindPipelineParametersResult = BindPipelineParametersPrivate(inputToOperateOn);
                }

                // We are successful at binding the pipeline input if there was a ScriptBlock to
                // run and it ran successfully or if we successfully bound a parameter based on
                // the pipeline input.

                result = (thereWasSomethingToBind && invokeScriptResult) || bindPipelineParametersResult;
            }
        }
        catch (ParameterBindingException)
        {
            // Reset the default values
            // This prevents the last pipeline object from being bound during EndProcessing
            // if it failed some post binding verification step.
            _context.RestoreDefaultParameterValues(_context.ParametersBoundThroughPipelineInput);

            // Let the parameter binding errors propagate out
            throw;
        }

        try
        {
            // Now make sure we have latched on to a single parameter set.
            _context.ParameterSetResolver.VerifyParameterSetSelected();
        }
        catch (ParameterBindingException)
        {
            // Reset the default values
            // This prevents the last pipeline object from being bound during EndProcessing
            // if it failed some post binding verification step.
            _context.RestoreDefaultParameterValues(_context.ParametersBoundThroughPipelineInput);

            throw;
        }

        if (!result)
        {
            // Reset the default values
            // This prevents the last pipeline object from being bound during EndProcessing
            // if it failed some post binding verification step.
            _context.RestoreDefaultParameterValues(_context.ParametersBoundThroughPipelineInput);
        }

        return result;
    }

    /// <summary>
    /// Binds the pipeline parameters using the specified input and parameter set.
    /// </summary>
    /// <param name="inputToOperateOn">
    /// The pipeline input to be bound to the parameters.
    /// </param>
    /// <exception cref="ParameterBindingException">
    /// If argument transformation fails.
    /// or
    /// The argument could not be coerced to the appropriate type for the parameter.
    /// or
    /// The parameter argument transformation, prerequisite, or validation failed.
    /// or
    /// If the binding to the parameter fails.
    /// or
    /// If there is a failure resetting values prior to binding from the pipeline
    /// </exception>
    /// <remarks>
    /// The algorithm for binding the pipeline object is as follows. If any
    /// step is successful true gets returned immediately.
    ///
    /// - If parameter supports ValueFromPipeline
    ///     - attempt to bind input value without type coercion
    /// - If parameter supports ValueFromPipelineByPropertyName
    ///     - attempt to bind the value of the property with the matching name without type coercion
    ///
    /// Now see if we have a single valid parameter set and reset the validParameterSets flags as
    /// necessary. If there are still multiple valid parameter sets, then we need to use TypeDistance
    /// to determine which parameters to do type coercion binding on.
    ///
    /// - If parameter supports ValueFromPipeline
    ///     - attempt to bind input value using type coercion
    /// - If parameter support ValueFromPipelineByPropertyName
    ///     - attempt to bind the vlue of the property with the matching name using type coercion
    /// </remarks>
    private bool BindPipelineParametersPrivate(PSObject inputToOperateOn)
    {
        if (ParameterBinderBase.bindingTracer.IsEnabled)
        {
            ConsolidatedString dontuseInternalTypeNames;
            ParameterBinderBase.bindingTracer.WriteLine(
                "PIPELINE object TYPE = [{0}]",
                inputToOperateOn == null || inputToOperateOn == AutomationNull.Value
                    ? "null"
                    : ((dontuseInternalTypeNames = inputToOperateOn.InternalTypeNames).Count > 0 && dontuseInternalTypeNames[0] != null)
                          ? dontuseInternalTypeNames[0]
                          : inputToOperateOn.BaseObject.GetType().FullName);

            ParameterBinderBase.bindingTracer.WriteLine("RESTORING pipeline parameter's original values");
        }

        bool result = false;

        // Reset the default values

        _context.RestoreDefaultParameterValues(_context.ParametersBoundThroughPipelineInput);

        // Now clear the parameter names from the previous pipeline input

        _context.ParametersBoundThroughPipelineInput.Clear();

        // --- Fast path: replay cached plan (Tasks 2.2 / 3.1 / 3.2) ---
        if (_cachedPlan is { } plan)
        {
            // Null / AutomationNull input — fall through to slow path (may not bind anything)
            if (inputToOperateOn == null || inputToOperateOn == AutomationNull.Value)
            {
                goto SlowPath;
            }

            // Type guard for ByPropertyName plans: if the input object type changed since the
            // plan was established, invalidate and fall through to slow path.
            if (plan.HasByPropertyName)
            {
                var currentType = GetPSObjectTypeName(inputToOperateOn);
                if (!string.Equals(currentType, plan.FirstObjectTypeName, StringComparison.Ordinal))
                {
                    if (ParameterBinderBase.bindingTracer.IsEnabled)
                    {
                        ParameterBinderBase.bindingTracer.WriteLine(
                            "PIPELINE BIND plan invalidated (type changed from '{0}' to '{1}')",
                            plan.FirstObjectTypeName, currentType);
                    }

                    _cachedPlan = null;
                    goto SlowPath;
                }
            }

            if (ParameterBinderBase.bindingTracer.IsEnabled)
            {
                ParameterBinderBase.bindingTracer.WriteLine(
                    "PIPELINE BIND via cached plan ({0} entries, set=0x{1:X})",
                    plan.Count, plan.ResolvedParameterSetFlag);
            }

            _context.ParameterSetResolver.CurrentParameterSetFlag = plan.ResolvedParameterSetFlag;

            bool fastResult = true;
            try
            {
                for (int fi = 0; fi < plan.Count; fi++)
                {
                    ref var entry = ref plan.Entries[fi];
                    bool bound = entry.IsValueFromPipeline
                        ? BindValueFromPipeline(inputToOperateOn, entry.Parameter, entry.Flags)
                        : BindValueFromPipelineByPropertyName(inputToOperateOn, entry.Parameter, entry.Flags);

                    if (!bound)
                    {
                        if (ParameterBinderBase.bindingTracer.IsEnabled)
                        {
                            ParameterBinderBase.bindingTracer.WriteLine(
                                "PIPELINE BIND plan invalidated (binding failure on entry {0})", fi);
                        }

                        fastResult = false;
                        break;
                    }
                }
            }
            catch (ParameterBindingException)
            {
                fastResult = false;
            }

            if (fastResult)
            {
                // Fast path succeeded — skip ValidateParameterSets and ApplyDefaultParameterBinding.
                return true;
            }

            // Fast path failed — invalidate plan, undo partial binds, fall through to slow path.
            _cachedPlan = null;
            _context.RestoreDefaultParameterValues(_context.ParametersBoundThroughPipelineInput);
            _context.ParametersBoundThroughPipelineInput.Clear();
        }

        SlowPath:

        // Now restore the parameter set flags

        _context.ParameterSetResolver.CurrentParameterSetFlag = _context.ParameterSetResolver.PrePipelineProcessingParameterSetFlags;
        uint validParameterSets = _context.ParameterSetResolver.CurrentParameterSetFlag;
        bool needToPrioritizeOneSpecificParameterSet = _context.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding != 0;
        int steps = needToPrioritizeOneSpecificParameterSet ? 2 : 1;

        if (needToPrioritizeOneSpecificParameterSet)
        {
            // ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding is set, so we are certain that the specified parameter set must be valid,
            // and it's not the only valid parameter set.
            Diagnostics.Assert((_context.ParameterSetResolver.CurrentParameterSetFlag & _context.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding) != 0, "ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding should be valid if it's set");
            validParameterSets = _context.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding;
        }

        for (int i = 0; i < steps; i++)
        {
            for (CurrentlyBinding currentlyBinding = CurrentlyBinding.ValueFromPipelineNoCoercion; currentlyBinding <= CurrentlyBinding.ValueFromPipelineByPropertyNameWithCoercion; ++currentlyBinding)
            {
                // The parameterBoundForCurrentlyBindingState will be true as long as there is one parameter gets bound, even if it belongs to AllSet
                bool parameterBoundForCurrentlyBindingState =
                    BindUnboundParametersForBindingState(
                        inputToOperateOn,
                        currentlyBinding,
                        validParameterSets);

                if (parameterBoundForCurrentlyBindingState)
                {
                    // Now validate the parameter sets again and update the valid sets.
                    // No need to validate the parameter sets and update the valid sets when dealing with the prioritized parameter set,
                    // this is because the prioritized parameter set is a single set, and when binding succeeds, ParameterSetResolver.CurrentParameterSetFlag
                    // must be equal to the specific prioritized parameter set.
                    if (!needToPrioritizeOneSpecificParameterSet || i == 1)
                    {
                        _context.ParameterSetResolver.ValidateParameterSets(true, true, _context.ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
                        validParameterSets = _context.ParameterSetResolver.CurrentParameterSetFlag;
                    }

                    result = true;
                }
            }

            // Update the validParameterSets after the binding attempt for the prioritized parameter set
            if (needToPrioritizeOneSpecificParameterSet && i == 0)
            {
                // If the prioritized set can be bound successfully, there is no need to do the second round binding
                if (_context.ParameterSetResolver.CurrentParameterSetFlag == _context.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding)
                {
                    break;
                }

                validParameterSets = _context.ParameterSetResolver.CurrentParameterSetFlag & (~_context.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding);
            }
        }

        // Now make sure we only have one valid parameter set
        // Note, this will throw if we have more than one.

        _context.ParameterSetResolver.ValidateParameterSets(false, true, _context.ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);

        if (!_context.DefaultParameterBindingInUse)
        {
            if (_context.ApplyDefaultParameterBinding(
                "PIPELINE BIND",
                isDynamic: false,
                currentParameterSetFlag: _context.ParameterSetResolver.CurrentParameterSetFlag))
            {
                _context.DefaultParameterBindingInUse = true;
            }
        }

        // Capture binding plan after the first successful slow-path bind (Task 2.1 / 4.2)
        if (result && _cachedPlan == null)
        {
            uint resolvedFlag = _context.ParameterSetResolver.CurrentParameterSetFlag;
            // Only cache when fully resolved to a single parameter set (single bit set)
            if (resolvedFlag != 0 && (resolvedFlag & (resolvedFlag - 1)) == 0)
            {
                var pipelineBound = _context.ParametersBoundThroughPipelineInput;
                int count = pipelineBound.Count;
                if (count > 0)
                {
                    bool hasByPropName = false;
                    var entries = new PipelineBindingPlan.Entry[count];
                    for (int i = 0; i < count; i++)
                    {
                        var param = pipelineBound[i];
                        bool isVfp = false;
                        foreach (ParameterSetSpecificMetadata meta in param.Parameter.GetMatchingParameterSetData(resolvedFlag))
                        {
                            if (meta.ValueFromPipeline)
                            {
                                isVfp = true;
                                break;
                            }

                            if (meta.ValueFromPipelineByPropertyName)
                            {
                                hasByPropName = true;
                                break;
                            }
                        }

                        entries[i] = new PipelineBindingPlan.Entry
                        {
                            Parameter = param,
                            IsValueFromPipeline = isVfp,
                            Flags = ParameterBindingFlags.None,
                        };
                    }

                    var newPlan = new PipelineBindingPlan
                    {
                        Entries = entries,
                        Count = count,
                        ResolvedParameterSetFlag = resolvedFlag,
                        DefaultParameterBindingApplied = _context.DefaultParameterBindingInUse,
                        HasByPropertyName = hasByPropName,
                        FirstObjectTypeName = hasByPropName ? GetPSObjectTypeName(inputToOperateOn) : null,
                    };

                    if (ParameterBinderBase.bindingTracer.IsEnabled)
                    {
                        ParameterBinderBase.bindingTracer.WriteLine(
                            "PIPELINE BIND plan created ({0} entries, set=0x{1:X}, hasByPropName={2})",
                            count, resolvedFlag, hasByPropName);
                    }

                    _cachedPlan = newPlan;
                }
            }
        }

        return result;
    }

#nullable enable
    /// <summary>
    /// Returns the first PS type name for <paramref name="pso"/>, used for fast-path type-guard validation.
    /// </summary>
    private static string? GetPSObjectTypeName(PSObject? pso)
    {
        if (pso == null || pso == AutomationNull.Value) return null;
        var typeNames = pso.InternalTypeNames;
        if (typeNames.Count > 0 && typeNames[0] != null) return typeNames[0];
        return pso.BaseObject.GetType().FullName;
    }
#nullable restore

    private bool BindUnboundParametersForBindingState(
        PSObject inputToOperateOn,
        CurrentlyBinding currentlyBinding,
        uint validParameterSets)
    {
        bool aParameterWasBound = false;

        // First check to see if the default parameter set has been defined and if it
        // is still valid.

        uint defaultParameterSetFlag = _context.DefaultParameterSetFlag;

        if (defaultParameterSetFlag != 0 && (validParameterSets & defaultParameterSetFlag) != 0)
        {
            // Since we have a default parameter set and it is still valid, give preference to the
            // parameters in the default set.

            aParameterWasBound =
                BindUnboundParametersForBindingStateInParameterSet(
                    inputToOperateOn,
                    currentlyBinding,
                    defaultParameterSetFlag);

            if (!aParameterWasBound)
            {
                validParameterSets &= ~(defaultParameterSetFlag);
            }
        }

        if (!aParameterWasBound)
        {
            // Since nothing was bound for the default parameter set, try all
            // the other parameter sets that are still valid.

            aParameterWasBound =
                BindUnboundParametersForBindingStateInParameterSet(
                    inputToOperateOn,
                    currentlyBinding,
                    validParameterSets);
        }

        s_tracer.WriteLine("aParameterWasBound = {0}", aParameterWasBound);
        return aParameterWasBound;
    }

    private bool BindUnboundParametersForBindingStateInParameterSet(
        PSObject inputToOperateOn,
        CurrentlyBinding currentlyBinding,
        uint validParameterSets)
    {
        bool aParameterWasBound = false;

        // For all unbound parameters in the parameter set, see if we can bind
        // from the input object directly from pipeline without type coercion.
        //
        // We loop the unbound parameters in reversed order, so that we can move
        // items from the unboundParameters collection to the boundParameters
        // collection as we process, without the need to make a copy of the
        // unboundParameters collection.
        //
        // We used to make a copy of UnboundParameters and loop from the head of the
        // list. Now we are processing the unbound parameters from the end of the list.
        // This change should NOT be a breaking change. The 'validParameterSets' in
        // this method never changes, so no matter we start from the head or the end of
        // the list, every unbound parameter in the list that takes pipeline input and
        // satisfy the 'validParameterSets' will be bound. If parameters from more than
        // one sets got bound, then "parameter set cannot be resolved" error will be thrown,
        // which is expected.

        var unboundParameters = _context.UnboundParameters;
        for (int i = unboundParameters.Count - 1; i >= 0; i--)
        {
            var parameter = unboundParameters[i];

            // if the parameter is never a pipeline parameter, don't consider it
            if (!parameter.Parameter.IsPipelineParameterInSomeParameterSet)
                continue;

            // if the parameter is not in the specified parameter set, don't consider it
            if ((validParameterSets & parameter.Parameter.ParameterSetFlags) == 0 &&
                !parameter.Parameter.IsInAllSets)
            {
                continue;
            }

            // Get the appropriate parameter set data
            var parameterSetData = parameter.Parameter.GetMatchingParameterSetData(validParameterSets);

            bool bindResult = false;

            foreach (ParameterSetSpecificMetadata parameterSetMetadata in parameterSetData)
            {
                // In the first phase we try to bind the value from the pipeline without
                // type coercion

                if (currentlyBinding == CurrentlyBinding.ValueFromPipelineNoCoercion &&
                    parameterSetMetadata.ValueFromPipeline)
                {
                    bindResult = BindValueFromPipeline(inputToOperateOn, parameter, ParameterBindingFlags.None);
                }
                // In the next phase we try binding the value from the pipeline by matching
                // the property name
                else if (currentlyBinding == CurrentlyBinding.ValueFromPipelineByPropertyNameNoCoercion &&
                    parameterSetMetadata.ValueFromPipelineByPropertyName &&
                    inputToOperateOn != null)
                {
                    bindResult = BindValueFromPipelineByPropertyName(inputToOperateOn, parameter, ParameterBindingFlags.None);
                }
                // The third step is to attempt to bind the value from the pipeline with
                // type coercion.
                else if (currentlyBinding == CurrentlyBinding.ValueFromPipelineWithCoercion &&
                    parameterSetMetadata.ValueFromPipeline)
                {
                    bindResult = BindValueFromPipeline(inputToOperateOn, parameter, ParameterBindingFlags.ShouldCoerceType);
                }
                // The final step is to attempt to bind the value from the pipeline by matching
                // the property name
                else if (currentlyBinding == CurrentlyBinding.ValueFromPipelineByPropertyNameWithCoercion &&
                    parameterSetMetadata.ValueFromPipelineByPropertyName &&
                    inputToOperateOn != null)
                {
                    bindResult = BindValueFromPipelineByPropertyName(inputToOperateOn, parameter, ParameterBindingFlags.ShouldCoerceType);
                }

                if (bindResult)
                {
                    aParameterWasBound = true;
                    break;
                }
            }
        }

        return aParameterWasBound;
    }

    private bool BindValueFromPipeline(
        PSObject inputToOperateOn,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags)
    {
        // Attempt binding the value from the pipeline
        // without type coercion

        ParameterBinderBase.bindingTracer.WriteLine(
            ((flags & ParameterBindingFlags.ShouldCoerceType) != 0) ?
                "Parameter [{0}] PIPELINE INPUT ValueFromPipeline WITH COERCION" :
                "Parameter [{0}] PIPELINE INPUT ValueFromPipeline NO COERCION",
            parameter.Parameter.Name);

        return BindPipelineParameterWithErrorHandling(inputToOperateOn, parameter, flags, ignoreInvalidCastTransformationError: true);
    }

    private bool BindValueFromPipelineByPropertyName(
        PSObject inputToOperateOn,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags)
    {
        bool bindResult = false;

        var messageFormat = ((flags & ParameterBindingFlags.ShouldCoerceType) != 0) ?
            "Parameter [{0}] PIPELINE INPUT ValueFromPipelineByPropertyName WITH COERCION" :
            "Parameter [{0}] PIPELINE INPUT ValueFromPipelineByPropertyName NO COERCION";
        ParameterBinderBase.bindingTracer.WriteLine(messageFormat, parameter.Parameter.Name);

        PSMemberInfo member = inputToOperateOn.Properties[parameter.Parameter.Name];

        if (member == null)
        {
            // Since a member matching the name of the parameter wasn't found,
            // check the aliases.

            foreach (string alias in parameter.Parameter.Aliases)
            {
                member = inputToOperateOn.Properties[alias];

                if (member != null)
                {
                    break;
                }
            }
        }

        if (member != null)
        {
            bindResult = BindPipelineParameterWithErrorHandling(member.Value, parameter, flags, ignoreInvalidCastTransformationError: false);
        }

        return bindResult;
    }

    private bool BindPipelineParameterWithErrorHandling(
        object inputValue,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags,
        bool ignoreInvalidCastTransformationError)
    {
        bool bindResult = false;
        ParameterBindingException parameterBindingException = null;

        try
        {
            bindResult = BindPipelineParameter(inputValue, parameter, flags);
        }
        catch (ParameterBindingArgumentTransformationException e)
        {
            if (ignoreInvalidCastTransformationError)
            {
                PSInvalidCastException invalidCast = e.InnerException is ArgumentTransformationMetadataException
                    ? e.InnerException.InnerException as PSInvalidCastException
                    : e.InnerException as PSInvalidCastException;

                if (invalidCast == null)
                {
                    parameterBindingException = e;
                }
            }
            else
            {
                parameterBindingException = e;
            }

            // Just ignore and continue.
            bindResult = false;
        }
        catch (ParameterBindingValidationException e)
        {
            parameterBindingException = e;
        }
        catch (ParameterBindingParameterDefaultValueException e)
        {
            parameterBindingException = e;
        }
        catch (ParameterBindingException)
        {
            // Just ignore and continue.
            bindResult = false;
        }

        if (parameterBindingException != null)
        {
            _context.ThrowOrElaborateBindingException(parameterBindingException);
        }

        return bindResult;
    }

    /// <summary>
    /// Binds the specified value to the specified parameter.
    /// </summary>
    /// <param name="parameterValue">
    /// The value to bind to the parameter
    /// </param>
    /// <param name="parameter">
    /// The parameter to bind the value to.
    /// </param>
    /// <param name="flags">
    /// Parameter binding flags for type coercion and validation.
    /// </param>
    /// <returns>
    /// True if the parameter was successfully bound. False if <paramref name="flags"/>
    /// specifies no coercion and the type does not match the parameter type.
    /// </returns>
    /// <exception cref="ParameterBindingParameterDefaultValueException">
    /// If the parameter binder encounters an error getting the default value.
    /// </exception>
    private bool BindPipelineParameter(
        object parameterValue,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags)
    {
        bool result = false;

        if (parameterValue != AutomationNull.Value)
        {
            s_tracer.WriteLine("Adding PipelineParameter name={0}; value={1}",
                             parameter.Parameter.Name, parameterValue ?? "null");

            // Backup the default value
            _context.BackupDefaultParameter(parameter);

            // Now bind the new value — rent from the per-invocation CPI pool to avoid allocation
            CommandParameterInternal param = _context.RentPipelineCpi();
            param.InitializeAsParameterWithArgument(
                /*parameterAst*/null, parameter.Parameter.Name, parameter.Parameter.ParameterText,
                /*argumentAst*/null, parameterValue,
                false);

            flags &= ~ParameterBindingFlags.DelayBindScriptBlock;
            result = _context.DispatchBindToSubBinder(_context.ParameterSetResolver.CurrentParameterSetFlag, param, parameter, flags);

            if (result)
            {
                // Now make sure to remember that the default value needs to be restored
                // if we get another pipeline object
                _context.ParametersBoundThroughPipelineInput.Add(parameter);
            }
        }

        return result;
    }
}
