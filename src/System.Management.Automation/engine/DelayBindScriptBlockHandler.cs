// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation.Language;

namespace System.Management.Automation;

/// <summary>
/// Encapsulates the delay-bind ScriptBlock deferral, invocation, and per-pipeline-object
/// result binding.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class DelayBindScriptBlockHandler
{
    /// <summary>
    /// Represents a ScriptBlock argument that has been deferred for per-pipeline-object evaluation.
    /// </summary>
    internal sealed class DelayedScriptBlockArgument
    {
        /// <summary>
        /// The handler that owns this entry. Used to determine whether to invoke the ScriptBlock
        /// (if this handler is the active one) or to use the already-computed result.
        /// </summary>
        internal DelayBindScriptBlockHandler _handler;

        internal CommandParameterInternal _argument;
        internal Collection<PSObject> _evaluatedArgument;

        public override string ToString() => _argument.ArgumentValue.ToString();
    }

    private readonly IBindingStateContext _stateContext;
    private readonly IBindingOperationsContext _opsContext;

    internal DelayBindScriptBlockHandler(IBindingStateContext stateContext, IBindingOperationsContext opsContext)
    {
        _stateContext = stateContext;
        _opsContext = opsContext;
    }

    private string DebuggerDisplayValue
        => $"DelayBindHandler: PendingCount={_stateContext.DelayBindScriptBlocks.Count}";

    /// <summary>Exposes the parameter keys that have pending delay-bind entries.</summary>
    internal ICollection<MergedCompiledCommandParameter> Keys => _stateContext.DelayBindScriptBlocks.Keys;

    /// <summary>
    /// Creates a new <see cref="DelayedScriptBlockArgument"/> owned by this handler.
    /// </summary>
    internal DelayedScriptBlockArgument CreateEntry(CommandParameterInternal argument)
        => new DelayedScriptBlockArgument { _argument = argument, _handler = this };

    /// <summary>
    /// Adds <paramref name="delayedArg"/> for <paramref name="parameter"/> if not already present.
    /// </summary>
    internal void TryAdd(MergedCompiledCommandParameter parameter, DelayedScriptBlockArgument delayedArg)
    {
        var delayedScriptBlocks = _stateContext.DelayBindScriptBlocks;
        if (!delayedScriptBlocks.ContainsKey(parameter))
        {
            delayedScriptBlocks.Add(parameter, delayedArg);
        }
    }

    /// <summary>
    /// Invokes any deferred ScriptBlocks against <paramref name="inputToOperateOn"/> and binds
    /// the resulting values to their associated parameters.
    /// </summary>
    /// <param name="inputToOperateOn">The current pipeline object.</param>
    /// <param name="thereWasSomethingToBind">
    /// Set to <see langword="true"/> if at least one ScriptBlock was deferred.
    /// </param>
    /// <returns><see langword="true"/> if all deferred ScriptBlocks bound successfully.</returns>
    internal bool InvokeAndBind(PSObject inputToOperateOn, out bool thereWasSomethingToBind)
    {
        thereWasSomethingToBind = false;
        bool result = true;
        var invocationInfo = _stateContext.InvocationInfo;

        // NOTE: we are not doing backup and restore of default parameter
        // values here.  It is not needed because each script block will be
        // invoked and each delay bind parameter bound for each pipeline object.
        // This is unlike normal pipeline object processing which may bind
        // different parameters depending on the type of the incoming pipeline
        // object.

        // Loop through each of the delay bind script blocks and invoke them.
        // Bind the result to the associated parameter

        foreach (KeyValuePair<MergedCompiledCommandParameter, DelayedScriptBlockArgument> delayedScriptBlock in _stateContext.DelayBindScriptBlocks)
        {
            thereWasSomethingToBind = true;

            CommandParameterInternal argument = delayedScriptBlock.Value._argument;
            MergedCompiledCommandParameter parameter = delayedScriptBlock.Key;

            ScriptBlock script = argument.ArgumentValue as ScriptBlock;

            Diagnostics.Assert(
                script != null,
                "An argument should only be put in the delayBindScriptBlocks collection if it is a ScriptBlock");

            Collection<PSObject> output = null;

            Exception error = null;
            using (ParameterBinderBase.bindingTracer.TraceScope(
                "Invoking delay-bind ScriptBlock"))
            {
                if (delayedScriptBlock.Value._handler == this)
                {
                    try
                    {
                        output = script.DoInvoke(inputToOperateOn, inputToOperateOn, Array.Empty<object>());
                        delayedScriptBlock.Value._evaluatedArgument = output;
                    }
                    catch (RuntimeException runtimeException)
                    {
                        error = runtimeException;
                    }
                }
                else
                {
                    output = delayedScriptBlock.Value._evaluatedArgument;
                }
            }

            if (error != null)
            {
                ParameterBindingException.ThrowScriptBlockArgumentInvocationFailed(
                    error,
                    invocationInfo,
                    _opsContext.GetErrorExtent(argument),
                    parameter.Parameter.Name,
                    null,
                    null,
                    error.Message);
            }

            if (output == null || output.Count == 0)
            {
                ParameterBindingException.ThrowScriptBlockArgumentNoOutput(
                    invocationInfo,
                    _opsContext.GetErrorExtent(argument),
                    parameter.Parameter.Name,
                    null);
            }

            // Check the output.  If it is only a single value, just pass the single value,
            // if not, pass in the whole collection.

            object newValue = output;
            if (output.Count == 1)
            {
                newValue = output[0];
            }

            // Create a new CommandParameterInternal for the output of the script block.
            var newArgument = CommandParameterInternal.CreateParameterWithArgument(
                argument.ParameterAst, argument.ParameterName, "-" + argument.ParameterName + ":",
                argument.ArgumentAst, newValue,
                false);

            if (!_opsContext.BindToAssociatedBinder(newArgument, parameter, ParameterBindingFlags.ShouldCoerceType))
            {
                result = false;
            }
        }

        return result;
    }
}
