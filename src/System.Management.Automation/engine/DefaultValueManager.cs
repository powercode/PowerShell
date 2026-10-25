// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Management.Automation.Language;

namespace System.Management.Automation;

/// <summary>
/// Provides the binding context that <see cref="DefaultValueManager"/> needs
/// from its owning parameter binder controller.
/// </summary>
internal interface IDefaultValueManagerContext
{
    /// <summary>Invocation info for the current command (used in exception messages).</summary>
    InvocationInfo InvocationInfo { get; }

    /// <summary>Returns the script extent for error reporting on the given argument.</summary>
    IScriptExtent GetErrorExtent(CommandParameterInternal argument);

    /// <summary>Returns the current default value of the named parameter from its backing binder.</summary>
    object? GetDefaultParameterValue(string name);

    /// <summary>Stores a parameter value back to its backing binder without running validation.</summary>
    bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter);

    /// <summary>Parameters already bound to a value, keyed by parameter name.</summary>
    Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; }

    /// <summary>Parameters not yet bound to a value.</summary>
    IList<MergedCompiledCommandParameter> UnboundParameters { get; }

    /// <summary>Arguments already matched to a parameter, keyed by parameter name.</summary>
    Dictionary<string, CommandParameterInternal> BoundArguments { get; }

    /// <summary>Returns a pipeline CPI to the pool after it is removed from BoundArguments.</summary>
    void ReturnPipelineCpi(CommandParameterInternal cpi);

    /// <summary>Saved default values for restoration after each pipeline object is processed.</summary>
    Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; }
}

/// <summary>
/// Manages the lifecycle of per-pipeline-object default parameter values:
/// saving script defaults, backing up current values before pipeline binding,
/// and restoring them after each pipeline object is processed.
/// </summary>
internal sealed class DefaultValueManager
{
    private readonly IDefaultValueManagerContext _context;

    internal DefaultValueManager(IDefaultValueManagerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Records a script parameter's default value so it can be restored after pipeline binding.
    /// Called from the <c>SaveDefaultScriptParameterValue</c> override on the controller.
    /// </summary>
    internal void SaveScriptParameterValue(string name, string parameterText, object value)
    {
        _context.DefaultParameterValues.Add(name,
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
        if (!_context.DefaultParameterValues.ContainsKey(parameter.Parameter.Name))
        {
            object? defaultParameterValue = _context.GetDefaultParameterValue(parameter.Parameter.Name);
            _context.DefaultParameterValues.Add(
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

        // Get all the matching arguments from the defaultParameterValues collection
        // and bind those that had parameters that were bound via pipeline input

        foreach (MergedCompiledCommandParameter parameter in parameters)
        {
            if (parameter == null)
            {
                continue;
            }

            // If the argument was found then bind it to the parameter
            // and manage the bound and unbound parameter list

            if (_context.DefaultParameterValues.TryGetValue(parameter.Parameter.Name, out CommandParameterInternal? argumentToBind))
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

                    bool bindResult = _context.RestoreParameter(argumentToBind, parameter);

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
                        _context.InvocationInfo,
                        _context.GetErrorExtent(argumentToBind),
                        parameter.Parameter.Name,
                        parameter.Parameter.Type,
                        specifiedType,
                        error.Message);
                }

                // Since the parameter was returned to its original value,
                // ensure that it is not in the boundParameters list but
                // is in the unboundParameters list

                _context.BoundParameters.Remove(parameter.Parameter.Name);

                if (_context.UnboundParameters.IndexOf(parameter) < 0)
                {
                    _context.UnboundParameters.Add(parameter);
                }

                if (_context.BoundArguments.Remove(parameter.Parameter.Name, out CommandParameterInternal? removedCpi))
                {
                    _context.ReturnPipelineCpi(removedCpi);
                }
            }
            else
            {
                // Since the parameter was not reset, ensure that the parameter
                // is in the bound parameters list and not in the unbound
                // parameters list

                if (!_context.BoundParameters.ContainsKey(parameter.Parameter.Name))
                {
                    _context.BoundParameters.Add(parameter.Parameter.Name, parameter);
                }

                // Ensure the parameter is not in the unboundParameters list

                BindingState.SwapRemove(_context.UnboundParameters, parameter);
            }
        }
    }
}
