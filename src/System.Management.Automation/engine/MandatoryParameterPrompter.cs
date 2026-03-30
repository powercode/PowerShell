// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Text;

namespace System.Management.Automation;

/// <summary>
/// Provides the callback that <see cref="MandatoryParameterPrompter"/> needs
/// from its owning parameter binder controller to bind prompted values.
/// </summary>
internal interface IMandatoryParameterPrompterContext
{
    /// <summary>Resolves and binds a named parameter after prompting.</summary>
    bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags);
}

/// <summary>
/// Encapsulates mandatory-parameter detection, user prompting, and prompt data structure building.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class MandatoryParameterPrompter
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

    private readonly ParameterSetResolver _parameterSetResolver;
    private readonly ExecutionContext _context;
    private readonly Cmdlet _command;
    private readonly IMandatoryParameterPrompterContext _bindingContext;

    private string DebuggerDisplayValue
    {
        get
        {
            string commandName = _command?.GetType().Name ?? "(unknown)";
            return $"MandatoryPrompter: {commandName}";
        }
    }

    internal MandatoryParameterPrompter(
        ParameterSetResolver parameterSetResolver,
        ExecutionContext context,
        Cmdlet command,
        IMandatoryParameterPrompterContext bindingContext)
    {
        _parameterSetResolver = parameterSetResolver;
        _context = context;
        _command = command;
        _bindingContext = bindingContext;
    }

    /// <summary>
    /// Checks for unbound mandatory parameters. If any are found, an exception is thrown.
    /// </summary>
    /// <param name="missingMandatoryParameters">
    /// Returns the missing mandatory parameters, if any.
    /// </param>
    /// <returns>
    /// True if there are no unbound mandatory parameters. False if there are unbound mandatory parameters.
    /// </returns>
    internal bool HandleUnboundMandatoryParameters(out Collection<MergedCompiledCommandParameter> missingMandatoryParameters)
    {
        return HandleUnboundMandatoryParameters(
            ParameterSetResolver.ValidParameterSetCount(_parameterSetResolver.CurrentParameterSetFlag),
            false,
            false,
            false,
            out missingMandatoryParameters);
    }

    /// <summary>
    /// Checks for unbound mandatory parameters. If any are found and promptForMandatory is true,
    /// the user will be prompted for the missing mandatory parameters.
    /// </summary>
    /// <param name="validParameterSetCount">
    /// The number of valid parameter sets.
    /// </param>
    /// <param name="processMissingMandatory">
    /// If true, unbound mandatory parameters will be processed via user prompting (if allowed by promptForMandatory).
    /// If false, unbound mandatory parameters will cause false to be returned.
    /// </param>
    /// <param name="promptForMandatory">
    /// If true, unbound mandatory parameters will cause the user to be prompted. If false, unbound
    /// mandatory parameters will cause an exception to be thrown.
    /// </param>
    /// <param name="isPipelineInputExpected">
    /// If true, then only parameters that don't take pipeline input will be prompted for.
    /// If false, any mandatory parameter that has not been specified will be prompted for.
    /// </param>
    /// <param name="missingMandatoryParameters">
    /// Returns the missing mandatory parameters, if any.
    /// </param>
    /// <returns>
    /// True if there are no unbound mandatory parameters. False if there are unbound mandatory parameters
    /// and promptForMandatory if false.
    /// </returns>
    /// <exception cref="ParameterBindingException">
    /// If prompting didn't result in a value for the parameter (only when <paramref name="promptForMandatory"/> is true.)
    /// </exception>
    internal bool HandleUnboundMandatoryParameters(
        int validParameterSetCount,
        bool processMissingMandatory,
        bool promptForMandatory,
        bool isPipelineInputExpected,
        out Collection<MergedCompiledCommandParameter> missingMandatoryParameters)
    {
        bool result = true;

        missingMandatoryParameters = _parameterSetResolver.GetMissingMandatoryParameters(validParameterSetCount, isPipelineInputExpected);

        if (missingMandatoryParameters.Count > 0)
        {
            if (processMissingMandatory)
            {
                // If the host interface wasn't specified or we were instructed not to prompt, then throw
                // an exception instead.
                if ((_context.EngineHostInterface == null) || (!promptForMandatory))
                {
                    Diagnostics.Assert(
                        _context.EngineHostInterface != null,
                        "The EngineHostInterface should never be null");

                    ParameterBinderBase.bindingTracer.WriteLine(
                        "ERROR: host does not support prompting for missing mandatory parameters");

                    string missingParameters = BuildMissingParamsString(missingMandatoryParameters);

                    ParameterBindingException.ThrowMissingMandatoryParameter(
                        _command.MyInvocation,
                        missingParameters);
                }

                // Create a collection to store the prompt descriptions of unbound mandatory parameters.
                Collection<FieldDescription> fieldDescriptionList = CreatePromptDataStructures(missingMandatoryParameters);

                Dictionary<string, PSObject> parameters =
                    PromptForMissingMandatoryParameters(
                        fieldDescriptionList,
                        missingMandatoryParameters);

                using (ParameterBinderBase.bindingTracer.TraceScope(
                    "BIND PROMPTED mandatory parameter args"))
                {
                    // Now bind any parameters that were retrieved.
                    foreach (KeyValuePair<string, PSObject> entry in parameters)
                    {
                        var argument =
                            CommandParameterInternal.CreateParameterWithArgument(
                            /*parameterAst*/null, entry.Key, "-" + entry.Key + ":",
                            /*argumentAst*/null, entry.Value,
                            false);

                        // Ignore the result since any failure should cause an exception.
                        result =
                            _bindingContext.ResolveAndBindNamedParameter(argument, ParameterBindingFlags.ShouldCoerceType | ParameterBindingFlags.ThrowOnParameterNotFound);

                        Diagnostics.Assert(
                            result,
                            "Any error in binding the parameter with type coercion should result in an exception");
                    }

                    result = true;
                }
            }
            else
            {
                result = false;
            }
        }

        return result;
    }

    private Dictionary<string, PSObject> PromptForMissingMandatoryParameters(
        Collection<FieldDescription> fieldDescriptionList,
        Collection<MergedCompiledCommandParameter> missingMandatoryParameters)
    {
        Dictionary<string, PSObject>? parameters = null;

        Exception? error = null;

        // Prompt
        try
        {
            ParameterBinderBase.bindingTracer.WriteLine(
                "PROMPTING for missing mandatory parameters using the host");
            string msg = ParameterBinderStrings.PromptMessage;
            InvocationInfo invoInfo = _command.MyInvocation;
            string caption = StringUtil.Format(ParameterBinderStrings.PromptCaption,
                invoInfo.MyCommand.Name,
                invoInfo.PipelinePosition);

            parameters = _context.EngineHostInterface.UI.Prompt(caption, msg, fieldDescriptionList);
        }
        catch (NotImplementedException notImplemented)
        {
            error = notImplemented;
        }
        catch (HostException hostException)
        {
            error = hostException;
        }
        catch (PSInvalidOperationException invalidOperation)
        {
            error = invalidOperation;
        }

        if (error != null)
        {
            ParameterBinderBase.bindingTracer.WriteLine(
                "ERROR: host does not support prompting for missing mandatory parameters");

            string missingParameters = BuildMissingParamsString(missingMandatoryParameters);

            ParameterBindingException.ThrowMissingMandatoryParameter(
                _command.MyInvocation,
                missingParameters);
        }

        if ((parameters == null) || (parameters.Count == 0))
        {
            ParameterBinderBase.bindingTracer.WriteLine(
                "ERROR: still missing mandatory parameters after PROMPTING");

            string missingParameters = BuildMissingParamsString(missingMandatoryParameters);

            ParameterBindingException.ThrowMissingMandatoryParameter(
                _command.MyInvocation,
                missingParameters);
        }

        return parameters;
    }

    internal static string BuildMissingParamsString(Collection<MergedCompiledCommandParameter> missingMandatoryParameters)
    {
        StringBuilder missingParameters = new StringBuilder();

        foreach (MergedCompiledCommandParameter missingParameter in missingMandatoryParameters)
        {
            missingParameters.Append(CultureInfo.InvariantCulture, $" {missingParameter.Parameter.Name}");
        }

        return missingParameters.ToString();
    }

    private Collection<FieldDescription> CreatePromptDataStructures(
        Collection<MergedCompiledCommandParameter> missingMandatoryParameters)
    {
        StringBuilder usedHotKeys = new StringBuilder();
        Collection<FieldDescription> fieldDescriptionList = new Collection<FieldDescription>();

        // See if any of the unbound parameters are mandatory.
        foreach (MergedCompiledCommandParameter parameter in missingMandatoryParameters)
        {
            ParameterSetSpecificMetadata parameterSetMetadata =
                parameter.Parameter.GetParameterSetData(_parameterSetResolver.CurrentParameterSetFlag);

            FieldDescription fDesc = new FieldDescription(parameter.Parameter.Name);

            string? helpInfo = null;

            try
            {
                helpInfo = parameterSetMetadata.GetHelpMessage(_command);
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentException)
            {
            }

            if (!string.IsNullOrEmpty(helpInfo))
            {
                fDesc.HelpMessage = helpInfo;
            }

            fDesc.SetParameterType(parameter.Parameter.Type);
            fDesc.Label = BuildLabel(parameter.Parameter.Name, usedHotKeys);

            foreach (ValidateArgumentsAttribute vaAttr in parameter.Parameter.ValidationAttributes)
            {
                fDesc.Attributes.Add(vaAttr);
            }

            foreach (ArgumentTransformationAttribute arAttr in parameter.Parameter.ArgumentTransformationAttributes)
            {
                fDesc.Attributes.Add(arAttr);
            }

            fDesc.IsMandatory = true;

            fieldDescriptionList.Add(fDesc);
        }

        return fieldDescriptionList;
    }

    /// <summary>
    /// Creates a label with a Hotkey from <paramref name="parameterName"/>. The Hotkey is
    /// <paramref name="parameterName"/>'s first capital character not in <paramref name="usedHotKeys"/>.
    /// If <paramref name="parameterName"/> does not have any capital character, the first lower
    ///  case character is used. The Hotkey is preceded by an ampersand in the label.
    /// </summary>
    /// <param name="parameterName">
    /// The parameter name from which the Hotkey is created
    /// </param>
    /// <param name="usedHotKeys">
    /// A list of used HotKeys
    /// </param>
    /// <returns>
    /// A label made from parameterName with a HotKey indicated by an ampersand
    /// </returns>
    private static string BuildLabel(string parameterName, StringBuilder usedHotKeys)
    {
        Diagnostics.Assert(!string.IsNullOrEmpty(parameterName), "parameterName is not set");
        const char hotKeyPrefix = '&';
        StringBuilder label = new StringBuilder(parameterName);
        string usedHotKeysStr = usedHotKeys.ToString();

        // Priority order: uppercase letters, lowercase letters, then non-letters.
        Func<char, bool>[] predicates = new Func<char, bool>[]
        {
            char.IsUpper,
            char.IsLower,
            static c => !char.IsLetter(c)
        };

        foreach (Func<char, bool> predicate in predicates)
        {
            for (int i = 0; i < parameterName.Length; i++)
            {
                if (predicate(parameterName[i]) && !usedHotKeysStr.Contains(parameterName[i]))
                {
                    label.Insert(i, hotKeyPrefix);
                    usedHotKeys.Append(parameterName[i]);
                    return label.ToString();
                }
            }
        }

        // Fallback when all candidates are already used.
        label.Insert(0, hotKeyPrefix);

        return label.ToString();
    }
}
