// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Management.Automation.Language;

namespace System.Management.Automation;

/// <summary>
/// Read/write access to live binding state: parameter collections, invocation info,
/// parameter-set state, command infrastructure, and per-pipeline-object state.
/// </summary>
internal interface IBindingStateContext
{
    // === Core binding collections ===

    /// <summary>Parameters not yet bound to a value.</summary>
    IList<MergedCompiledCommandParameter> UnboundParameters { get; }

    /// <summary>Parameters already bound to a value, keyed by name.</summary>
    Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; }

    /// <summary>Arguments already matched to a parameter, keyed by parameter name.</summary>
    Dictionary<string, CommandParameterInternal> BoundArguments { get; }

    /// <summary>Arguments not yet matched to a parameter.</summary>
    List<CommandParameterInternal> UnboundArguments { get; set; }

    /// <summary>Parameters bound through pipeline input in the current pipeline iteration.</summary>
    List<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; }

    // === Invocation info ===

    /// <summary>The invocation info for trace and exception context.</summary>
    InvocationInfo InvocationInfo { get; }

    // === Parameter set state ===

    /// <summary>The parameter-set resolver for the current command.</summary>
    ParameterSetResolver ParameterSetResolver { get; }

    /// <summary>Current valid parameter set flags.</summary>
    uint CurrentParameterSetFlag { get; }

    /// <summary>Default parameter set flag; writable so the handler can update it after merging dynamic parameter metadata.</summary>
    uint DefaultParameterSetFlag { get; set; }

    /// <summary>Name of the default parameter set.</summary>
    string DefaultParameterSetName { get; }

    /// <summary>The name of the command being bound (for tracing).</summary>
    string CommandName { get; }

    // === Command infrastructure ===

    /// <summary>Whether the command implements <see cref="IDynamicParameters"/>.</summary>
    bool ImplementsDynamicParameters { get; }

    /// <summary>The cmdlet instance being bound.</summary>
    Cmdlet Command { get; }

    /// <summary>The engine execution context.</summary>
    ExecutionContext Context { get; }

    /// <summary>The merged parameter metadata including all sub-binders.</summary>
    MergedCommandParameterMetadata BindableParameters { get; }

    /// <summary>Command-line parameter tracker shared across binders.</summary>
    CommandLineParameters CommandLineParameters { get; }

    // === Default parameter binding state ===

    /// <summary>Whether $PSDefaultParameterValues binding is currently in use.</summary>
    bool DefaultParameterBindingInUse { get; set; }

    /// <summary>Names of parameters bound via $PSDefaultParameterValues.</summary>
    List<string> BoundDefaultParameters { get; }

    /// <summary>Alias list for the current cmdlet (cached per invocation).</summary>
    List<string>? DefaultParameterAliasList { get; set; }

    /// <summary>Set of warning keys already emitted for default-binding failures (prevents duplicates).</summary>
    HashSet<string> DefaultParameterWarningSet { get; }

    /// <summary>Cached result of the last successful <c>GetDefaultParameterValuePairs</c> call.</summary>
    Dictionary<MergedCompiledCommandParameter, object>? AllDefaultParameterValuePairs { get; set; }

    /// <summary>Whether default parameter binding should be attempted for the current invocation.</summary>
    bool UseDefaultParameterBinding { get; set; }

    // === Pipeline per-object state ===

    /// <summary>Pending delay-bind ScriptBlock entries keyed by parameter.</summary>
    Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; }

    /// <summary>Saved default values for restoration after each pipeline object is processed.</summary>
    Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; }
}

/// <summary>
/// Binding dispatch operations: re-parsing, named/positional binding, sub-binder dispatch,
/// default value management, and pipeline operations.
/// </summary>
internal interface IBindingOperationsContext
{
    // === Parameter set operations ===

    /// <summary>Sets the resolved parameter set name on the command.</summary>
    void SetParameterSetName(string parameterSetName);

    /// <summary>Throws or elaborates a parameter binding exception.</summary>
    void ThrowOrElaborateBindingException(ParameterBindingException exception);

    // === Bind dispatch ===

    /// <summary>Dispatches a bind call to the appropriate sub-binder.</summary>
    bool DispatchBindToSubBinder(
        uint validParameterSetFlag,
        CommandParameterInternal argument,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags);

    /// <summary>Binds an argument to a parameter via the appropriate sub-binder.</summary>
    bool BindToAssociatedBinder(
        CommandParameterInternal argument,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags);

    /// <summary>Resolves and binds a named parameter after prompting.</summary>
    bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags);

    // === Re-parse and positional ===

    /// <summary>Re-parses unbound arguments to pair parameter names with following values.</summary>
    void ReparseUnboundArguments();

    /// <summary>Binds named parameters from <paramref name="args"/> against the current parameter set.</summary>
    void BindNamedParameters(uint parameterSetFlag, List<CommandParameterInternal> args);

    /// <summary>Binds positional parameters from <paramref name="args"/>.</summary>
    void BindPositionalParameters(
        List<CommandParameterInternal> args,
        uint currentParameterSetFlag,
        uint defaultParameterSetFlag,
        out ParameterBindingException? outgoingBindingException);

    // === Error extent ===

    /// <summary>Returns the script extent for error reporting on the given argument.</summary>
    IScriptExtent GetErrorExtent(CommandParameterInternal argument);

    // === Default value management ===

    /// <summary>Returns the current default value of the named parameter from its backing binder.</summary>
    object? GetDefaultParameterValue(string name);

    /// <summary>Stores a parameter value back to its backing binder without running validation.</summary>
    bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter);

    /// <summary>Copy of bound positional parameter names from the default binder.</summary>
    HashSet<string> CopyBoundPositionalParameters();

    // === Pipeline operations ===

    /// <summary>Invokes any delay-bind ScriptBlocks and binds the evaluated result.</summary>
    bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind);

    /// <summary>Backs up the default value of <paramref name="parameter"/> before pipeline binding overwrites it.</summary>
    void BackupDefaultParameter(MergedCompiledCommandParameter parameter);

    /// <summary>Restores the default values of <paramref name="parameters"/> that were overwritten during pipeline binding.</summary>
    void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters);

    /// <summary>Rents a <see cref="CommandParameterInternal"/> from the pipeline CPI pool.</summary>
    CommandParameterInternal RentPipelineCpi();

    /// <summary>Returns a pipeline CPI to the pool after it is removed from BoundArguments.</summary>
    void ReturnPipelineCpi(CommandParameterInternal cpi);

    /// <summary>Attempts to apply $PSDefaultParameterValues bindings for any yet-unbound parameters.</summary>
    bool ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag);
}
