// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace System.Management.Automation;

/// <summary>
/// Encapsulates $PSDefaultParameterValues lookup, qualification, and binding.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class DefaultParameterValueBinder
{
    [TraceSource("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).")]
    private static readonly PSTraceSource s_tracer =
        PSTraceSource.GetTracer("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).");

    [TraceSource("ParameterBinding", "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.")]
    internal static readonly PSTraceSource bindingTracer =
        PSTraceSource.GetTracer("ParameterBinding", "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.", false);

    private const string Separator = ":::";

    private readonly CommandMetadata _commandMetadata;
    private readonly MshCommandRuntime _commandRuntime;
    private readonly ExecutionContext _context;
    private readonly MergedCommandParameterMetadata _bindableParameters;
    private readonly IBindingStateContext _stateContext;
    private readonly IBindingOperationsContext _opsContext;

    internal DefaultParameterValueBinder(
        CommandMetadata commandMetadata,
        MshCommandRuntime commandRuntime,
        ExecutionContext context,
        MergedCommandParameterMetadata bindableParameters,
        IBindingStateContext stateContext,
        IBindingOperationsContext opsContext)
    {
        _commandMetadata = commandMetadata;
        _commandRuntime = commandRuntime;
        _context = context;
        _bindableParameters = bindableParameters;
        _stateContext = stateContext;
        _opsContext = opsContext;
    }

    /// <summary>Whether default parameter binding has successfully bound at least one parameter.</summary>
    internal bool DefaultParameterBindingInUse { get; set; }

    private string DebuggerDisplayValue
    {
        get
        {
            int pairCount = _stateContext.AllDefaultParameterValuePairs?.Count ?? 0;
            return $"DefaultValueBinder: InUse={DefaultParameterBindingInUse}, Pairs={pairCount}";
        }
    }

    /// <summary>The $PSDefaultParameterValues hashtable from the session.</summary>
    internal IDictionary? DefaultParameterValues { get; set; }

    internal void ResetForNewBinding()
    {
        _stateContext.DefaultParameterWarningSet.Clear();
        DefaultParameterBindingInUse = false;
        _stateContext.UseDefaultParameterBinding = true;
    }

    /// <summary>
    /// Apply the binding for the default parameter defined by the user.
    /// </summary>
    internal bool ApplyDefaultParameterBinding(string bindingStage, bool isDynamic, uint currentParameterSetFlag)
    {
        if (!_stateContext.UseDefaultParameterBinding)
        {
            return false;
        }

        if (isDynamic)
        {
            // Get user-defined default parameter value pairs again so that dynamic
            // parameter value pairs are included.
            _stateContext.AllDefaultParameterValuePairs = GetDefaultParameterValuePairs(false);
        }

        Dictionary<MergedCompiledCommandParameter, object>? qualifiedParameterValuePairs = GetQualifiedParameterValuePairs(currentParameterSetFlag, _stateContext.AllDefaultParameterValuePairs);

        if (qualifiedParameterValuePairs == null)
        {
            return false;
        }

        bool isSuccess;
        using (bindingTracer.TraceScope("BIND DEFAULT <parameter, value> pairs after [{0}] for [{1}]", bindingStage, _commandMetadata.Name))
        {
            isSuccess = BindDefaultParameters(currentParameterSetFlag, qualifiedParameterValuePairs);
            if (isSuccess && !DefaultParameterBindingInUse)
            {
                DefaultParameterBindingInUse = true;
            }
        }

        s_tracer.WriteLine("BIND DEFAULT after [{0}] result [{1}]", bindingStage, isSuccess);
        return isSuccess;
    }

    /// <summary>
    /// Bind the default parameter value pairs.
    /// </summary>
    /// <returns>
    /// True if at least one default parameter was bound successfully; otherwise false.
    /// </returns>
    internal bool BindDefaultParameters(
        uint validParameterSetFlag,
        Dictionary<MergedCompiledCommandParameter, object> defaultParameterValues)
    {
        bool ret = false;
        var warningSet = _stateContext.DefaultParameterWarningSet;
        foreach (var pair in defaultParameterValues)
        {
            MergedCompiledCommandParameter parameter = pair.Key;
            object argumentValue = pair.Value;
            string parameterName = parameter.Parameter.Name;

            try
            {
                if (argumentValue is ScriptBlock scriptBlockArg)
                {
                    // Pass the current binding state as the script block argument.
                    PSObject arg = WrapBindingState();
                    Collection<PSObject> results = scriptBlockArg.Invoke(arg);
                    if (results is null or [])
                    {
                        continue;
                    }

                    argumentValue = results.Count == 1 ? results[0] : results;
                }

                CommandParameterInternal bindableArgument =
                    CommandParameterInternal.CreateParameterWithArgument(parameterAst: null, parameterName, $"-{parameterName}:", argumentAst: null, argumentValue, false);

                bool bindResult = _opsContext.DispatchBindToSubBinder(validParameterSetFlag, bindableArgument, parameter,
                    ParameterBindingFlags.ShouldCoerceType | ParameterBindingFlags.DelayBindScriptBlock);

                if (bindResult && !ret)
                {
                    ret = true;
                }

                if (bindResult)
                {
                    _stateContext.BoundDefaultParameters.Add(parameterName);
                }
            }
            catch (ParameterBindingException ex)
            {
                // Failures in default binding should not affect command-line binding.
                if (!warningSet.Contains(_commandMetadata.Name + Separator + parameterName))
                {
                    string message = string.Format(
                        CultureInfo.InvariantCulture,
                        ParameterBinderStrings.FailToBindDefaultParameter,
                        LanguagePrimitives.IsNull(argumentValue) ? "null" : argumentValue.ToString(),
                        parameterName,
                        ex.Message);
                    _commandRuntime.WriteWarning(message);
                    warningSet.Add(_commandMetadata.Name + Separator + parameterName);
                }
            }
        }

        return ret;
    }

    /// <summary>
    /// Wrap current binding state to provide scriptblock default values with context.
    /// </summary>
    private PSObject WrapBindingState()
    {
        var boundParameters = _stateContext.BoundParameters;
        HashSet<string> boundParameterNames = new(boundParameters.Count, StringComparer.OrdinalIgnoreCase);
        HashSet<string> boundPositionalParameterNames = _opsContext.CopyBoundPositionalParameters();
        HashSet<string> boundDefaultParameterNames = new(boundParameters.Count, StringComparer.OrdinalIgnoreCase);

        foreach (string paramName in boundParameters.Keys)
        {
            boundParameterNames.Add(paramName);
        }

        foreach (string paramName in _stateContext.BoundDefaultParameters)
        {
            boundDefaultParameterNames.Add(paramName);
        }

        PSObject result = new();
        result.Properties.Add(new PSNoteProperty("BoundParameters", boundParameterNames));
        result.Properties.Add(new PSNoteProperty("BoundPositionalParameters", boundPositionalParameterNames));
        result.Properties.Add(new PSNoteProperty("BoundDefaultParameters", boundDefaultParameterNames));

        return result;
    }

    /// <summary>
    /// Get all qualified default parameter value pairs based on the
    /// current parameter set flag.
    /// </summary>
    internal Dictionary<MergedCompiledCommandParameter, object>? GetQualifiedParameterValuePairs(
        uint currentParameterSetFlag,
        Dictionary<MergedCompiledCommandParameter, object>? availableParameterValuePairs)
    {
        if (availableParameterValuePairs == null)
        {
            return null;
        }

        Dictionary<MergedCompiledCommandParameter, object> result = new();

        uint possibleParameterFlag = uint.MaxValue;
        foreach (var pair in availableParameterValuePairs)
        {
            MergedCompiledCommandParameter parameter = pair.Key;
            if ((parameter.Parameter.ParameterSetFlags & currentParameterSetFlag) == 0 && !parameter.Parameter.IsInAllSets)
            {
                continue;
            }

            if (_stateContext.BoundArguments.ContainsKey(parameter.Parameter.Name))
            {
                continue;
            }

            // Check if this parameter's sets conflict with other possible parameters.
            if (parameter.Parameter.ParameterSetFlags != 0)
            {
                possibleParameterFlag &= parameter.Parameter.ParameterSetFlags;
                if (possibleParameterFlag == 0)
                {
                    return null;
                }
            }

            result.Add(parameter, pair.Value);
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Get aliases for the current cmdlet.
    /// </summary>
    private List<string>? GetAliasOfCurrentCmdlet()
    {
        var results = _context.SessionState.Internal.GetAliasesByCommandName(_commandMetadata.Name).ToList();
        return results.Count > 0 ? results : null;
    }

    /// <summary>
    /// Check if the specified alias matches any alias of the current cmdlet.
    /// </summary>
    private bool MatchAnyAlias(string aliasName)
    {
        if (_stateContext.DefaultParameterAliasList == null)
        {
            return false;
        }

        WildcardPattern aliasPattern = WildcardPattern.Get(aliasName, WildcardOptions.IgnoreCase);
        foreach (string alias in _stateContext.DefaultParameterAliasList)
        {
            if (aliasPattern.IsMatch(alias))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get all available default parameter value pairs.
    /// </summary>
    /// <returns>Qualified pairs or null if none are available.</returns>
    internal Dictionary<MergedCompiledCommandParameter, object>? GetDefaultParameterValuePairs(bool needToGetAlias)
    {
        if (DefaultParameterValues == null)
        {
            _stateContext.UseDefaultParameterBinding = false;
            _stateContext.AllDefaultParameterValuePairs = null;
            return null;
        }

        Dictionary<MergedCompiledCommandParameter, object> availablePairs = new(DefaultParameterValues.Count);

        if (needToGetAlias && DefaultParameterValues.Count > 0)
        {
            _stateContext.DefaultParameterAliasList = GetAliasOfCurrentCmdlet();
        }

        // Reset to enabled by default for each parse pass.
        _stateContext.UseDefaultParameterBinding = true;

        string currentCmdletName = _commandMetadata.Name;

        IDictionary<string, MergedCompiledCommandParameter> bindableParameters = _bindableParameters.BindableParameters;
        IDictionary<string, MergedCompiledCommandParameter> bindableAlias = _bindableParameters.AliasedParameters;

        // Parameters that received conflicting values and should be ignored.
        var parametersToRemove = new HashSet<MergedCompiledCommandParameter>();
        var wildcardDefault = new Dictionary<string, object>();
        var keysToRemove = new List<object>();

        foreach (DictionaryEntry entry in DefaultParameterValues)
        {
            if (entry.Key is not string key)
            {
                continue;
            }

            key = key.Trim();
            string? cmdletName = null;
            string? parameterName = null;

            if (!DefaultParameterDictionary.CheckKeyIsValid(key, ref cmdletName, ref parameterName))
            {
                if (key.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && LanguagePrimitives.IsTrue(entry.Value))
                {
                    _stateContext.UseDefaultParameterBinding = false;
                    _stateContext.AllDefaultParameterValuePairs = null;
                    return null;
                }

                if (!key.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(entry.Key);
                }

                continue;
            }

            Diagnostics.Assert(cmdletName != null && parameterName != null, "The cmdletName and parameterName should be set in CheckKeyIsValid");

            if (WildcardPattern.ContainsWildcardCharacters(key))
            {
                wildcardDefault.Add(cmdletName + Separator + parameterName, entry.Value!);
                continue;
            }

            // Continue only if cmdletName matches this cmdlet name or one of its aliases.
            if (!cmdletName.Equals(currentCmdletName, StringComparison.OrdinalIgnoreCase) && !MatchAnyAlias(cmdletName))
            {
                continue;
            }

            GetDefaultParameterValuePairsHelper(
                cmdletName,
                parameterName,
                entry.Value!,
                bindableParameters,
                bindableAlias,
                availablePairs,
                parametersToRemove);
        }

        foreach (KeyValuePair<string, object> wildcard in wildcardDefault)
        {
            string key = wildcard.Key;

            string cmdletName = key.Substring(0, key.IndexOf(Separator, StringComparison.OrdinalIgnoreCase));
            string parameterName = key.Substring(key.IndexOf(Separator, StringComparison.OrdinalIgnoreCase) + Separator.Length);

            WildcardPattern cmdletPattern = WildcardPattern.Get(cmdletName, WildcardOptions.IgnoreCase);
            if (!cmdletPattern.IsMatch(currentCmdletName) && !MatchAnyAlias(cmdletName))
            {
                continue;
            }

            if (!WildcardPattern.ContainsWildcardCharacters(parameterName))
            {
                GetDefaultParameterValuePairsHelper(
                    cmdletName,
                    parameterName,
                    wildcard.Value,
                    bindableParameters,
                    bindableAlias,
                    availablePairs,
                    parametersToRemove);

                continue;
            }

            WildcardPattern parameterPattern = MemberMatch.GetNamePattern(parameterName);
            var matches = new List<MergedCompiledCommandParameter>();

            foreach (KeyValuePair<string, MergedCompiledCommandParameter> entry in bindableParameters)
            {
                if (parameterPattern.IsMatch(entry.Key))
                {
                    matches.Add(entry.Value);
                }
            }

            foreach (KeyValuePair<string, MergedCompiledCommandParameter> entry in bindableAlias)
            {
                if (parameterPattern.IsMatch(entry.Key))
                {
                    matches.Add(entry.Value);
                }
            }

            if (matches.Count > 1)
            {
                if (!_stateContext.DefaultParameterWarningSet.Contains(cmdletName + Separator + parameterName))
                {
                    _commandRuntime.WriteWarning(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            ParameterBinderStrings.MultipleParametersMatched,
                            parameterName));
                    _stateContext.DefaultParameterWarningSet.Add(cmdletName + Separator + parameterName);
                }

                continue;
            }

            if (matches.Count == 1)
            {
                if (!availablePairs.ContainsKey(matches[0]))
                {
                    availablePairs.Add(matches[0], wildcard.Value);
                    continue;
                }

                if (!wildcard.Value.Equals(availablePairs[matches[0]]))
                {
                    if (!_stateContext.DefaultParameterWarningSet.Contains(cmdletName + Separator + parameterName))
                    {
                        _commandRuntime.WriteWarning(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                ParameterBinderStrings.DifferentValuesAssignedToSingleParameter,
                                parameterName));
                        _stateContext.DefaultParameterWarningSet.Add(cmdletName + Separator + parameterName);
                    }

                    parametersToRemove.Add(matches[0]);
                }
            }
        }

        if (keysToRemove.Count > 0)
        {
            var keysInError = new StringBuilder();
            foreach (object badFormatKey in keysToRemove)
            {
                if (DefaultParameterValues.Contains(badFormatKey))
                {
                    DefaultParameterValues.Remove(badFormatKey);
                }

                keysInError.Append(badFormatKey).Append(", ");
            }

            keysInError.Remove(keysInError.Length - 2, 2);
            bool multipleKeys = keysToRemove.Count > 1;
            string formatString = multipleKeys
                ? ParameterBinderStrings.MultipleKeysInBadFormat
                : ParameterBinderStrings.SingleKeyInBadFormat;
            _commandRuntime.WriteWarning(
                string.Format(CultureInfo.InvariantCulture, formatString, keysInError));
        }

        foreach (MergedCompiledCommandParameter parameter in parametersToRemove)
        {
            availablePairs.Remove(parameter);
        }

        _stateContext.AllDefaultParameterValuePairs = availablePairs.Count > 0 ? availablePairs : null;
        return _stateContext.AllDefaultParameterValuePairs;
    }

    /// <summary>
    /// Helper method for <see cref="GetDefaultParameterValuePairs"/>.
    /// </summary>
    private void GetDefaultParameterValuePairsHelper(
        string cmdletName,
        string paramName,
        object paramValue,
        IDictionary<string, MergedCompiledCommandParameter> bindableParameters,
        IDictionary<string, MergedCompiledCommandParameter> bindableAlias,
        Dictionary<MergedCompiledCommandParameter, object> result,
        HashSet<MergedCompiledCommandParameter> parametersToRemove)
    {
        // No exception should be thrown if no match is found because paramName
        // may refer to a dynamic parameter not yet introduced at this stage.
        bool writeWarning = false;
        if (bindableParameters.TryGetValue(paramName, out MergedCompiledCommandParameter? matchParameter) 
            || bindableAlias.TryGetValue(paramName, out matchParameter))
        {
            if (!result.TryGetValue(matchParameter, out object? resultObject))
            {
                result.Add(matchParameter, paramValue);
                return;
            }

            if (!paramValue.Equals(resultObject))
            {
                writeWarning = true;
                parametersToRemove.Add(matchParameter);
            }
        }

        if (writeWarning && !_stateContext.DefaultParameterWarningSet.Contains(cmdletName + Separator + paramName))
        {
            _commandRuntime.WriteWarning(string.Format(CultureInfo.InvariantCulture, ParameterBinderStrings.DifferentValuesAssignedToSingleParameter, paramName));
            _stateContext.DefaultParameterWarningSet.Add(cmdletName + Separator + paramName);
        }
    }
}
