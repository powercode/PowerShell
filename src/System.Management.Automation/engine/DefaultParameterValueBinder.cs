// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Linq;
using System.Text;

namespace System.Management.Automation;

/// <summary>
/// Provides the binding context that <see cref="DefaultParameterValueBinder"/> needs
/// from its owning parameter binder controller.
/// </summary>
internal interface IDefaultParameterBindingContext
{
    /// <summary>Dispatches a bind call to the appropriate sub-binder.</summary>
    bool DispatchBindToSubBinder(
        uint validParameterSetFlag,
        CommandParameterInternal argument,
        MergedCompiledCommandParameter parameter,
        ParameterBindingFlags flags);

    /// <summary>Arguments already bound, keyed by parameter name.</summary>
    Dictionary<string, CommandParameterInternal> BoundArguments { get; }

    /// <summary>Parameters already bound to a value, keyed by name.</summary>
    Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; }

    /// <summary>Names of parameters bound via $PSDefaultParameterValues.</summary>
    Collection<string> BoundDefaultParameters { get; }

    /// <summary>Copy of bound positional parameter names from the default binder.</summary>
    HashSet<string> CopyBoundPositionalParameters();
}

/// <summary>
/// Encapsulates $PSDefaultParameterValues lookup, qualification, and binding.
/// Extracted from <see cref="CmdletParameterBinderController"/> to satisfy SRP.
/// </summary>
internal sealed class DefaultParameterValueBinder
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

    private const string Separator = ":::";

    private readonly CommandMetadata _commandMetadata;
    private readonly MshCommandRuntime _commandRuntime;
    private readonly ExecutionContext _context;
    private readonly MergedCommandParameterMetadata _bindableParameters;
    private readonly IDefaultParameterBindingContext _bindingContext;

    private List<string> _aliasList;
    private readonly HashSet<string> _warningSet = new HashSet<string>();
    private Dictionary<MergedCompiledCommandParameter, object> _allDefaultParameterValuePairs;
    private bool _useDefaultParameterBinding = true;

    internal DefaultParameterValueBinder(
        CommandMetadata commandMetadata,
        MshCommandRuntime commandRuntime,
        ExecutionContext context,
        MergedCommandParameterMetadata bindableParameters,
        IDefaultParameterBindingContext bindingContext)
    {
        _commandMetadata = commandMetadata;
        _commandRuntime = commandRuntime;
        _context = context;
        _bindableParameters = bindableParameters;
        _bindingContext = bindingContext;
    }

    /// <summary>Whether default parameter binding has successfully bound at least one parameter.</summary>
    internal bool DefaultParameterBindingInUse { get; set; }

    /// <summary>The $PSDefaultParameterValues hashtable from the session.</summary>
    internal IDictionary DefaultParameterValues { get; set; }

    internal void ResetForNewBinding()
    {
        _warningSet.Clear();
        DefaultParameterBindingInUse = false;
        _useDefaultParameterBinding = true;
    }

    /// <summary>
    /// Apply the binding for the default parameter defined by the user.
    /// </summary>
    internal bool ApplyDefaultParameterBinding(string bindingStage, bool isDynamic, uint currentParameterSetFlag)
    {
        if (!_useDefaultParameterBinding)
        {
            return false;
        }

        if (isDynamic)
        {
            // Get user-defined default parameter value pairs again so that dynamic
            // parameter value pairs are included.
            _allDefaultParameterValuePairs = GetDefaultParameterValuePairs(false);
        }

        Dictionary<MergedCompiledCommandParameter, object> qualifiedParameterValuePairs =
            GetQualifiedParameterValuePairs(currentParameterSetFlag, _allDefaultParameterValuePairs);

        if (qualifiedParameterValuePairs == null)
        {
            return false;
        }

        bool isSuccess;
        using (bindingTracer.TraceScope(
            "BIND DEFAULT <parameter, value> pairs after [{0}] for [{1}]",
            bindingStage,
            _commandMetadata.Name))
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
                    if (results == null || results.Count == 0)
                    {
                        continue;
                    }

                    argumentValue = results.Count == 1 ? results[0] : results;
                }

                CommandParameterInternal bindableArgument =
                    CommandParameterInternal.CreateParameterWithArgument(
                        /*parameterAst*/null,
                        parameterName,
                        "-" + parameterName + ":",
                        /*argumentAst*/null,
                        argumentValue,
                        false);

                bool bindResult = _bindingContext.DispatchBindToSubBinder(
                    validParameterSetFlag,
                    bindableArgument,
                    parameter,
                    ParameterBindingFlags.ShouldCoerceType | ParameterBindingFlags.DelayBindScriptBlock);

                if (bindResult && !ret)
                {
                    ret = true;
                }

                if (bindResult)
                {
                    _bindingContext.BoundDefaultParameters.Add(parameterName);
                }
            }
            catch (ParameterBindingException ex)
            {
                // Failures in default binding should not affect command-line binding.
                if (!_warningSet.Contains(_commandMetadata.Name + Separator + parameterName))
                {
                    string message = string.Format(
                        CultureInfo.InvariantCulture,
                        ParameterBinderStrings.FailToBindDefaultParameter,
                        LanguagePrimitives.IsNull(argumentValue) ? "null" : argumentValue.ToString(),
                        parameterName,
                        ex.Message);
                    _commandRuntime.WriteWarning(message);
                    _warningSet.Add(_commandMetadata.Name + Separator + parameterName);
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
        HashSet<string> boundParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> boundPositionalParameterNames = _bindingContext.CopyBoundPositionalParameters();
        HashSet<string> boundDefaultParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string paramName in _bindingContext.BoundParameters.Keys)
        {
            boundParameterNames.Add(paramName);
        }

        foreach (string paramName in _bindingContext.BoundDefaultParameters)
        {
            boundDefaultParameterNames.Add(paramName);
        }

        PSObject result = new PSObject();
        result.Properties.Add(new PSNoteProperty("BoundParameters", boundParameterNames));
        result.Properties.Add(new PSNoteProperty("BoundPositionalParameters", boundPositionalParameterNames));
        result.Properties.Add(new PSNoteProperty("BoundDefaultParameters", boundDefaultParameterNames));

        return result;
    }

    /// <summary>
    /// Get all qualified default parameter value pairs based on the
    /// current parameter set flag.
    /// </summary>
    internal Dictionary<MergedCompiledCommandParameter, object> GetQualifiedParameterValuePairs(
        uint currentParameterSetFlag,
        Dictionary<MergedCompiledCommandParameter, object> availableParameterValuePairs)
    {
        if (availableParameterValuePairs == null)
        {
            return null;
        }

        Dictionary<MergedCompiledCommandParameter, object> result =
            new Dictionary<MergedCompiledCommandParameter, object>();

        uint possibleParameterFlag = uint.MaxValue;
        foreach (var pair in availableParameterValuePairs)
        {
            MergedCompiledCommandParameter parameter = pair.Key;
            if ((parameter.Parameter.ParameterSetFlags & currentParameterSetFlag) == 0 && !parameter.Parameter.IsInAllSets)
            {
                continue;
            }

            if (_bindingContext.BoundArguments.ContainsKey(parameter.Parameter.Name))
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
    private List<string> GetAliasOfCurrentCmdlet()
    {
        var results = _context.SessionState.Internal.GetAliasesByCommandName(_commandMetadata.Name).ToList();
        return results.Count > 0 ? results : null;
    }

    /// <summary>
    /// Check if the specified alias matches any alias of the current cmdlet.
    /// </summary>
    private bool MatchAnyAlias(string aliasName)
    {
        if (_aliasList == null)
        {
            return false;
        }

        WildcardPattern aliasPattern = WildcardPattern.Get(aliasName, WildcardOptions.IgnoreCase);
        foreach (string alias in _aliasList)
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
    internal Dictionary<MergedCompiledCommandParameter, object> GetDefaultParameterValuePairs(bool needToGetAlias)
    {
        if (DefaultParameterValues == null)
        {
            _useDefaultParameterBinding = false;
            _allDefaultParameterValuePairs = null;
            return null;
        }

        var availablePairs = new Dictionary<MergedCompiledCommandParameter, object>();

        if (needToGetAlias && DefaultParameterValues.Count > 0)
        {
            _aliasList = GetAliasOfCurrentCmdlet();
        }

        // Reset to enabled by default for each parse pass.
        _useDefaultParameterBinding = true;

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
            string cmdletName = null;
            string parameterName = null;

            if (!DefaultParameterDictionary.CheckKeyIsValid(key, ref cmdletName, ref parameterName))
            {
                if (key.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && LanguagePrimitives.IsTrue(entry.Value))
                {
                    _useDefaultParameterBinding = false;
                    _allDefaultParameterValuePairs = null;
                    return null;
                }

                if (!key.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(entry.Key);
                }

                continue;
            }

            Diagnostics.Assert(
                cmdletName != null && parameterName != null,
                "The cmdletName and parameterName should be set in CheckKeyIsValid");

            if (WildcardPattern.ContainsWildcardCharacters(key))
            {
                wildcardDefault.Add(cmdletName + Separator + parameterName, entry.Value);
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
                entry.Value,
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
                if (!_warningSet.Contains(cmdletName + Separator + parameterName))
                {
                    _commandRuntime.WriteWarning(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            ParameterBinderStrings.MultipleParametersMatched,
                            parameterName));
                    _warningSet.Add(cmdletName + Separator + parameterName);
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
                    if (!_warningSet.Contains(cmdletName + Separator + parameterName))
                    {
                        _commandRuntime.WriteWarning(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                ParameterBinderStrings.DifferentValuesAssignedToSingleParameter,
                                parameterName));
                        _warningSet.Add(cmdletName + Separator + parameterName);
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

        _allDefaultParameterValuePairs = availablePairs.Count > 0 ? availablePairs : null;
        return _allDefaultParameterValuePairs;
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
        MergedCompiledCommandParameter matchParameter;
        object resultObject;
        if (bindableParameters.TryGetValue(paramName, out matchParameter))
        {
            if (!result.TryGetValue(matchParameter, out resultObject))
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
        else if (bindableAlias.TryGetValue(paramName, out matchParameter))
        {
            if (!result.TryGetValue(matchParameter, out resultObject))
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

        if (writeWarning && !_warningSet.Contains(cmdletName + Separator + paramName))
        {
            _commandRuntime.WriteWarning(
                string.Format(
                    CultureInfo.InvariantCulture,
                    ParameterBinderStrings.DifferentValuesAssignedToSingleParameter,
                    paramName));
            _warningSet.Add(cmdletName + Separator + paramName);
        }
    }
}
