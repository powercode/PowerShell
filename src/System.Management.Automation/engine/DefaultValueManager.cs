// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Management.Automation.Language;

namespace System.Management.Automation;

/// <summary>
/// Manages the lifecycle of per-pipeline-object default parameter values:
/// saving script defaults, backing up current values before pipeline binding,
/// and restoring them after each pipeline object is processed.
/// </summary>
internal sealed class DefaultValueManager
{
    private readonly IBindingStateContext _stateContext;
    private readonly IBindingOperationsContext _opsContext;

    internal DefaultValueManager(IBindingStateContext stateContext, IBindingOperationsContext opsContext)
    {
        _stateContext = stateContext;
        _opsContext = opsContext;
    }

    /// <summary>
    /// Records a script parameter's default value so it can be restored after pipeline binding.
    /// Called from the <c>SaveDefaultScriptParameterValue</c> override on the controller.
    /// </summary>
    internal void SaveScriptParameterValue(string name, string parameterText, object value)
    {
        _stateContext.DefaultParameterValues.Add(name,
            CommandParameterInternal.CreateParameterWithArgument(
                /*parameterAst*/null, name, parameterText,
                /*argumentAst*/null, value,
                false));
    }

    /// <summary>
    /// Saves the current value of <paramref name="parameter"/> before pipeline binding may overwrite it.
    /// </summary>
    internal void Backup(MergedCompiledCommandParameter parameter)
    {
        var defaultParameterValues = _stateContext.DefaultParameterValues;
        if (!defaultParameterValues.ContainsKey(parameter.Parameter.Name))
        {
            object? defaultParameterValue = _opsContext.GetDefaultParameterValue(parameter.Parameter.Name);
            defaultParameterValues.Add(
                parameter.Parameter.Name,
                CommandParameterInternal.CreateParameterWithArgument(
                    /*parameterAst*/null, parameter.Parameter.Name, parameter.Parameter.ParameterText,
                    /*argumentAst*/null, defaultParameterValue,
                    false));
        }
    }

    /// <summary>
    /// Restores the saved default values for each parameter in <paramref name="parameters"/>
    /// after a pipeline object has been processed.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="parameters"/> is null.
    /// </exception>
    internal void Restore(IEnumerable<MergedCompiledCommandParameter> parameters)
    {
        if (parameters == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(parameters));
        }

        var boundParameters = _stateContext.BoundParameters;
        var unboundParameters = _stateContext.UnboundParameters;

        foreach (MergedCompiledCommandParameter parameter in parameters)
        {
            if (parameter == null)
            {
                continue;
            }

            // If the argument was found then bind it to the parameter
            // and manage the bound and unbound parameter list

            if (_stateContext.DefaultParameterValues.TryGetValue(parameter.Parameter.Name, out CommandParameterInternal? argumentToBind))
            {
                // Don't go through the normal binding routine to run data generation,
                // type coercion, validation, or prerequisites since we know the
                // type is already correct, and we don't want data generation to
                // run when resetting the default value.

                Exception? error = null;
                try
                {
                    // We shouldn't have to coerce the type here so its
                    // faster to pass false

                    bool bindResult = _opsContext.RestoreParameter(argumentToBind, parameter);

                    Diagnostics.Assert(
                        bindResult,
                        "Restoring the default value should not require type coercion");
                }
                catch (SetValueException setValueException)
                {
                    error = setValueException;
                }

                if (error != null)
                {
                    Type? specifiedType = argumentToBind.ArgumentValue?.GetType();
                    ParameterBindingException.ThrowParameterBindingFailed(
                        error,
                        _stateContext.InvocationInfo,
                        _opsContext.GetErrorExtent(argumentToBind),
                        parameter.Parameter.Name,
                        parameter.Parameter.Type,
                        specifiedType,
                        error.Message);
                }

                // Since the parameter was returned to its original value,
                // ensure that it is not in the boundParameters list but
                // is in the unboundParameters list

                boundParameters.Remove(parameter.Parameter.Name);

                if (unboundParameters.IndexOf(parameter) < 0)
                {
                    unboundParameters.Add(parameter);
                }

                if (_stateContext.BoundArguments.Remove(parameter.Parameter.Name, out CommandParameterInternal? removedCpi))
                {
                    _opsContext.ReturnPipelineCpi(removedCpi);
                }
            }
            else
            {
                // Since the parameter was not reset, ensure that the parameter
                // is in the bound parameters list and not in the unbound
                // parameters list

                if (!boundParameters.ContainsKey(parameter.Parameter.Name))
                {
                    boundParameters.Add(parameter.Parameter.Name, parameter);
                }

                // Ensure the parameter is not in the unboundParameters list

                ParameterBindingState.SwapRemove(unboundParameters, parameter);
            }
        }
    }
}
