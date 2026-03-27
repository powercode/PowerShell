// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;

using Dbg = System.Management.Automation.Diagnostics;

namespace System.Management.Automation
{
    /// <summary>
    /// Flags.
    /// </summary>
    [Flags]
    internal enum ParameterBindingFlags
    {
        /// <summary>
        /// No flags specified.
        /// </summary>
        None = 0,

        /// <summary>
        /// Set when the argument should be converted to the parameter type.
        /// </summary>
        ShouldCoerceType = 0x01,

        /// <summary>
        /// Set when the argument should not be validated or recorded in BoundParameters.
        /// </summary>
        IsDefaultValue = 0x02,

        /// <summary>
        /// Set when script blocks can be bound as a script block parameter instead of a normal argument.
        /// </summary>
        DelayBindScriptBlock = 0x04,

        /// <summary>
        /// Set when an exception will be thrown if a matching parameter could not be found.
        /// </summary>
        ThrowOnParameterNotFound = 0x08,
    }

    /// <summary>
    /// An abstract class used by the CommandProcessor to bind parameters to a bindable object.
    /// Derived classes are used to provide specific binding behavior for different object types,
    /// like Cmdlet, PsuedoParameterCollection, and dynamic parameter objects.
    /// </summary>
    /// <remarks>
    /// Use <see cref="InvocationInfo"/> directly for errors attributable to the command invocation
    /// itself rather than a specific parameter token.
    /// </remarks>
    [DebuggerDisplay("Command = {command}")]
    internal abstract class ParameterBinderBase
    {
        #region tracer
        [TraceSource("ParameterBinderBase", "A abstract helper class for the CommandProcessor that binds parameters to the specified object.")]
        private static readonly PSTraceSource s_tracer = PSTraceSource.GetTracer("ParameterBinderBase", "A abstract helper class for the CommandProcessor that binds parameters to the specified object.");

        [TraceSource("ParameterBinding", "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.")]
        internal static readonly PSTraceSource bindingTracer =
            PSTraceSource.GetTracer(
                "ParameterBinding",
                "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.",
                false);

        #endregion tracer

        #region ctor

        /// <summary>
        /// Constructs the parameter binder with the specified type metadata. The binder is only valid
        /// for a single instance of a bindable object and only for the duration of a command.
        /// </summary>
        /// <param name="target">
        /// The target object that the parameter values will be bound to.
        /// </param>
        /// <param name="invocationInfo">
        /// The invocation information for the code that is being bound.
        /// </param>
        /// <param name="context">
        /// The context of the currently running engine.
        /// </param>
        /// <param name="command">
        /// The command that the parameter binder is binding to. The command can be null.
        /// </param>
        internal ParameterBinderBase(
            object target,
            InvocationInfo invocationInfo,
            ExecutionContext context,
            InternalCommand command)
        {
            Diagnostics.Assert(target != null, "caller to verify target is not null.");
            Diagnostics.Assert(invocationInfo != null, "caller to verify invocationInfo is not null.");
            Diagnostics.Assert(context != null, "caller to verify context is not null.");

            bindingTracer.ShowHeaders = false;

            _command = command;
            _target = target;
            _invocationInfo = invocationInfo;
            _context = context;
            _engine = context.EngineIntrinsics;
            _isTranscribing = context.EngineHostInterface.UI.IsTranscribing;
            _typeCoercer = new ParameterTypeCoercer(invocationInfo, context, command);
        }

        /// <summary>
        /// Constructs the parameter binder with the specified type metadata. The binder is only valid
        /// for a single instance of a bindable object and only for the duration of a command.
        /// </summary>
        /// <param name="invocationInfo">
        /// The invocation information for the code that is being bound.
        /// </param>
        /// <param name="context">
        /// The context of the currently running engine.
        /// </param>
        /// <param name="command">
        /// The command that the parameter binder is binding to. The command can be null.
        /// </param>
        internal ParameterBinderBase(
            InvocationInfo invocationInfo,
            ExecutionContext context,
            InternalCommand command)
        {
            Diagnostics.Assert(invocationInfo != null, "caller to verify invocationInfo is not null.");
            Diagnostics.Assert(context != null, "caller to verify context is not null.");

            bindingTracer.ShowHeaders = false;

            _command = command;
            _invocationInfo = invocationInfo;
            _context = context;
            _engine = context.EngineIntrinsics;
            _isTranscribing = context.EngineHostInterface.UI.IsTranscribing;
            _typeCoercer = new ParameterTypeCoercer(invocationInfo, context, command);
        }

        #endregion ctor

        #region internal members

        /// <summary>
        /// Gets or sets the bindable object that the binder will bind parameters to.
        /// </summary>
        /// <value></value>
        internal object Target
        {
            get
            {
                Diagnostics.Assert(
                    _target != null,
                    "The target should always be set for the binder");

                return _target;
            }

            set
            {
                _target = value;
            }
        }

        /// <summary>
        /// The bindable object that parameters will be bound to.
        /// </summary>
        private object _target;

        /// <summary>
        /// Holds the set of parameters that have been bound from the command line...
        /// </summary>
        internal CommandLineParameters CommandLineParameters
        {
            get { return _commandLineParameters ??= new CommandLineParameters(); }

            // Setter is needed to pass into RuntimeParameterBinder instances
            set { _commandLineParameters = value; }
        }

        private CommandLineParameters _commandLineParameters;

        /// <summary>
        /// If this is true, then we want to record the list of bound parameters...
        /// </summary>
        internal bool RecordBoundParameters = true;

        /// <summary>
        /// Full Qualified ID for the obsolete parameter warning.
        /// </summary>
        internal const string FQIDParameterObsolete = "ParameterObsolete";

        #region Parameter default values

        /// <summary>
        /// Derived classes must override this method to get the default parameter
        /// value so that it can be restored between pipeline input.
        /// </summary>
        /// <param name="name">
        /// The name of the parameter to get the default value of.
        /// </param>
        /// <returns>
        /// The value of the parameter specified by name.
        /// </returns>
        internal abstract object GetDefaultParameterValue(string name);

        #endregion Parameter default values

        #region Parameter binding

        /// <summary>
        /// Derived classes define this method to bind the specified value
        /// to the specified parameter.
        /// </summary>
        /// <param name="name">
        ///     The name of the parameter to bind the value to.
        /// </param>
        /// <param name="value">
        ///     The value to bind to the parameter. It should be assumed by
        ///     derived classes that the proper type coercion has already taken
        ///     place and that any validation metadata has been satisfied.
        /// </param>
        /// <param name="parameterMetadata"></param>
        internal abstract void StoreParameterValue(string name, object value, CompiledCommandParameter parameterMetadata);

        private void ValidatePSTypeName(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            bool retryOtherBindingAfterFailure,
            object parameterValue)
        {
            Dbg.Assert(parameter != null, "Caller should verify parameter != null");
            Dbg.Assert(parameterMetadata != null, "Caller should verify parameterMetadata != null");

            if (parameterValue == null)
            {
                return;
            }

            IEnumerable<string> psTypeNamesOfArgumentValue = PSObject.AsPSObject(parameterValue).InternalTypeNames;
            string psTypeNameRequestedByParameter = parameterMetadata.PSTypeName;

            if (!psTypeNamesOfArgumentValue.Contains(psTypeNameRequestedByParameter, StringComparer.OrdinalIgnoreCase))
            {
                ParameterBindingException.ThrowMismatchedPSTypeName(
                    this.InvocationInfo,
                    GetErrorExtent(parameter),
                    (_invocationInfo != null) && (_invocationInfo.MyCommand != null) ? _invocationInfo.MyCommand.Name : string.Empty,
                    parameterMetadata.Name,
                    parameterMetadata.Type,
                    parameterValue.GetType(),
                    psTypeNameRequestedByParameter,
                    retryOtherBindingAfterFailure);
            }
        }

        /// <summary>
        /// Does all the type coercion, data generation, and validation necessary to bind the
        /// parameter, then calls the protected <see cref="StoreParameterValue(string, object, CompiledCommandParameter)"/>
        /// method to have the derived class do the actual binding.
        /// </summary>
        /// <param name="parameter">
        /// The parameter to be bound.
        /// </param>
        /// <param name="parameterMetadata">
        /// The metadata for the parameter to use in guiding the binding.
        /// </param>
        /// <param name="flags">
        /// Flags for type coercion and validation.
        /// </param>
        /// <returns>
        /// True if the parameter was successfully bound. False if <paramref name="coerceTypeIfNeeded"/>
        /// is false and the type does not match the parameter type.
        /// </returns>
        /// <remarks>
        /// The binding algorithm goes as follows:
        /// <list type="number">
        /// <item><description>The data generation attributes are run.</description></item>
        /// <item><description>The data is coerced into the correct type.</description></item>
        /// <item><description>The data is validated using the validation attributes.</description></item>
        /// <item><description>The data is encoded into the bindable object using the protected <see cref="StoreParameterValue(string, object, CompiledCommandParameter)"/> method.</description></item>
        /// </list>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="parameter"/> or <paramref name="parameterMetadata"/> is null.
        /// </exception>
        /// <exception cref="ParameterBindingException">
        /// If argument transformation fails.
        /// or
        /// The argument could not be coerced to the appropriate type for the parameter.
        /// or
        /// The parameter argument transformation, prerequisite, or validation failed.
        /// or
        /// If the binding to the parameter fails.
        /// </exception>
        internal virtual bool CoerceValidateAndBind(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            ParameterBindingFlags flags)
        {
            if (parameter == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(parameter));
            }

            if (parameterMetadata == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(parameterMetadata));
            }

            using (bindingTracer.TraceScope(
                       "BIND arg [{0}] to parameter [{1}]",
                       parameter.ArgumentValue,
                       parameterMetadata.Name))
            {
                // Set the complete parameter name

                parameter.ParameterName = parameterMetadata.Name;

                object parameterValue = parameter.ArgumentValue;

                parameterValue = ApplyArgumentTransformations(parameter, parameterMetadata, parameterValue, flags);

                bool shouldContinueBinding;
                parameterValue = ApplyTypeCoercion(parameter, parameterMetadata, parameterValue, flags, out shouldContinueBinding);
                if (!shouldContinueBinding)
                {
                    RecordBindingResult(parameter, parameterValue, result: false);
                    return false;
                }

                RunValidationPipeline(parameter, parameterMetadata, parameterValue, flags);
                WarnIfObsolete(parameterMetadata, flags);

                bool result = DispatchBind(parameter, parameterMetadata, parameterValue);
                RecordBindingResult(parameter, parameterValue, result);
                return result;
            }
        }

        /// <summary>
        /// Apply argument transformation attributes and return the transformed value.
        /// </summary>
        private object ApplyArgumentTransformations(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            object parameterValue,
            ParameterBindingFlags flags)
        {
            bool coerceTypeIfNeeded = (flags & ParameterBindingFlags.ShouldCoerceType) != 0;
            bool isDefaultValue = (flags & ParameterBindingFlags.IsDefaultValue) != 0;

            ScriptParameterBinder spb = this as ScriptParameterBinder;
            bool usesCmdletBinding = spb != null && spb.Script.UsesCmdletBinding;

            // No transformation is done for default values in script when the value is null and optional.
            foreach (ArgumentTransformationAttribute dma in parameterMetadata.ArgumentTransformationAttributes)
            {
                using (bindingTracer.TraceScope(
                    "Executing DATA GENERATION metadata: [{0}]",
                    dma.GetType()))
                {
                    try
                    {
                        if (dma is ArgumentTypeConverterAttribute argumentTypeConverter)
                        {
                            if (coerceTypeIfNeeded)
                            {
                                parameterValue = argumentTypeConverter.Transform(_engine, parameterValue, true, usesCmdletBinding);
                            }
                        }
                        else if ((parameterValue != null) ||
                                 (!isDefaultValue && (parameterMetadata.IsMandatoryInSomeParameterSet ||
                                                      parameterMetadata.CannotBeNull ||
                                                      dma.TransformNullOptionalParameters)))
                        {
                            parameterValue = dma.TransformInternal(_engine, parameterValue);
                        }

                        bindingTracer.WriteLine(
                            "result returned from DATA GENERATION: {0}",
                            parameterValue);
                    }
                    catch (Exception e) // Catch-all OK, 3rd party callout
                    {
                        bindingTracer.WriteLine(
                            "ERROR: DATA GENERATION: {0}",
                            e.Message);

                        ParameterBindingArgumentTransformationException.ThrowParameterArgumentTransformationError(
                            e,
                            this.InvocationInfo,
                            GetErrorExtent(parameter),
                            parameterMetadata.Name,
                            parameterMetadata.Type,
                            parameterValue?.GetType(),
                            e.Message);
                    }
                }
            }

            return parameterValue;
        }

        /// <summary>
        /// Apply type coercion (or compatibility checks for uncoerced binding) and return the value.
        /// </summary>
        private object ApplyTypeCoercion(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            object parameterValue,
            ParameterBindingFlags flags,
            out bool shouldContinueBinding)
        {
            bool coerceTypeIfNeeded = (flags & ParameterBindingFlags.ShouldCoerceType) != 0;
            if (coerceTypeIfNeeded)
            {
                shouldContinueBinding = true;
                return _typeCoercer.CoerceTypeAsNeeded(
                    parameter,
                    parameterMetadata.Name,
                    parameterMetadata.Type,
                    parameterMetadata.CollectionTypeInformation,
                    parameterValue);
            }

            shouldContinueBinding = ShouldContinueUncoercedBind(parameter, parameterMetadata, flags, ref parameterValue);
            return parameterValue;
        }

        /// <summary>
        /// Run type-name and validation-attribute checks, including mandatory null-or-empty validation.
        /// </summary>
        private void RunValidationPipeline(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            object parameterValue,
            ParameterBindingFlags flags)
        {
            bool coerceTypeIfNeeded = (flags & ParameterBindingFlags.ShouldCoerceType) != 0;
            bool isDefaultValue = (flags & ParameterBindingFlags.IsDefaultValue) != 0;

            if ((parameterMetadata.PSTypeName != null) && (parameterValue != null))
            {
                IEnumerable parameterValueAsEnumerable = LanguagePrimitives.GetEnumerable(parameterValue);
                if (parameterValueAsEnumerable != null)
                {
                    foreach (object o in parameterValueAsEnumerable)
                    {
                        this.ValidatePSTypeName(parameter, parameterMetadata, !coerceTypeIfNeeded, o);
                    }
                }
                else
                {
                    this.ValidatePSTypeName(parameter, parameterMetadata, !coerceTypeIfNeeded, parameterValue);
                }
            }

            // No validation is done for default values in script.
            if (isDefaultValue)
            {
                return;
            }

            for (int i = 0; i < parameterMetadata.ValidationAttributes.Length; i++)
            {
                var validationAttribute = parameterMetadata.ValidationAttributes[i];

                using (bindingTracer.TraceScope(
                    "Executing VALIDATION metadata: [{0}]",
                    validationAttribute.GetType()))
                {
                    try
                    {
                        validationAttribute.InternalValidate(parameterValue, _engine);
                    }
                    catch (Exception e) // Catch-all OK, 3rd party callout
                    {
                        bindingTracer.WriteLine(
                            "ERROR: VALIDATION FAILED: {0}",
                            e.Message);

                        ParameterBindingValidationException.ThrowParameterArgumentValidationError(
                            e,
                            this.InvocationInfo,
                            GetErrorExtent(parameter),
                            parameterMetadata.Name,
                            parameterMetadata.Type,
                            parameterValue?.GetType(),
                            e.Message);
                    }

                    s_tracer.WriteLine("Validation attribute on {0} returned {1}.", parameterMetadata.Name, false);
                }
            }

            // If the value is null, empty string, or empty collection, ensure binding can continue.
            if (parameterMetadata.IsMandatoryInSomeParameterSet)
            {
                ValidateNullOrEmptyArgument(parameter, parameterMetadata, parameterMetadata.Type, parameterValue, true);
            }
        }

        /// <summary>
        /// Emit warning for obsolete parameters when binding simple scripts/functions.
        /// </summary>
        private void WarnIfObsolete(
            CompiledCommandParameter parameterMetadata,
            ParameterBindingFlags flags)
        {
            bool isDefaultValue = (flags & ParameterBindingFlags.IsDefaultValue) != 0;
            ScriptParameterBinder spb = this as ScriptParameterBinder;
            bool usesCmdletBinding = spb != null && spb.Script.UsesCmdletBinding;

            if (parameterMetadata.ObsoleteAttribute == null || isDefaultValue || spb == null || usesCmdletBinding)
            {
                return;
            }

            string obsoleteWarning = string.Format(
                CultureInfo.InvariantCulture,
                ParameterBinderStrings.UseOfDeprecatedParameterWarning,
                parameterMetadata.Name,
                parameterMetadata.ObsoleteAttribute.Message);

            var mshCommandRuntime = this.Command.commandRuntime as MshCommandRuntime;

            // Keep warning behavior aligned with obsolete command/cmdlet parameter warnings.
            mshCommandRuntime?.WriteWarning(new WarningRecord(FQIDParameterObsolete, obsoleteWarning));
        }

        /// <summary>
        /// Bind the transformed value and surface any binding failure as <see cref="ParameterBindingException"/>.
        /// </summary>
        private bool DispatchBind(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            object parameterValue)
        {
            Exception bindError = null;

            try
            {
                StoreParameterValue(parameter.ParameterName, parameterValue, parameterMetadata);
            }
            catch (SetValueException setValueException)
            {
                bindError = setValueException;
            }

            if (bindError != null)
            {
                Type specifiedType = parameterValue?.GetType();
                ParameterBindingException.ThrowParameterBindingFailed(
                    bindError,
                    this.InvocationInfo,
                    GetErrorExtent(parameter),
                    parameterMetadata.Name,
                    parameterMetadata.Type,
                    specifiedType,
                    bindError.Message);
            }

            return true;
        }

        /// <summary>
        /// Record tracing and runtime logging information for the bind result.
        /// </summary>
        private void RecordBindingResult(CommandParameterInternal parameter, object parameterValue, bool result)
        {
            bindingTracer.WriteLine(
                "BIND arg [{0}] to param [{1}] {2}",
                parameterValue,
                parameter.ParameterName,
                result ? "SUCCESSFUL" : "SKIPPED");

            if (!result)
            {
                return;
            }

            if (RecordBoundParameters)
            {
                this.CommandLineParameters.Add(parameter.ParameterName, parameterValue);
            }

            MshCommandRuntime cmdRuntime = this.Command.commandRuntime as MshCommandRuntime;
            if ((cmdRuntime == null) ||
                (!cmdRuntime.LogPipelineExecutionDetail && !_isTranscribing) ||
                (cmdRuntime.PipelineProcessor == null))
            {
                return;
            }

            string stringToPrint = null;
            try
            {
                IEnumerable values = LanguagePrimitives.GetEnumerable(parameterValue);
                if (values != null)
                {
                    var sb = new Text.StringBuilder(256);
                    var sep = string.Empty;
                    foreach (var value in values)
                    {
                        sb.Append(sep);
                        sep = ", ";
                        sb.Append(value);
                        if (sb.Length > 256)
                        {
                            sb.Append(", ...");
                            break;
                        }
                    }

                    stringToPrint = sb.ToString();
                }
                else if (parameterValue != null)
                {
                    stringToPrint = parameterValue.ToString();
                }
            }
            catch (Exception) // Catch-all OK, 3rd party callout
            {
            }

            if (stringToPrint != null)
            {
                cmdRuntime.PipelineProcessor.LogExecutionParameterBinding(this.InvocationInfo, parameter.ParameterName, stringToPrint);
            }
        }

        /// <summary>
        /// This method ensures that if the parameter is mandatory, and AllowNull, AllowEmptyString,
        /// and/or AllowEmptyCollection is not specified, then argument is not null or empty.
        /// </summary>
        /// <param name="parameter">
        /// The argument token.
        /// </param>
        /// <param name="parameterMetadata">
        /// The metadata for the parameter.
        /// </param>
        /// <param name="argumentType">
        /// The type of the argument to validate against.
        /// </param>
        /// <param name="parameterValue">
        /// The value that will be bound to the parameter.
        /// </param>
        /// <param name="recurseIntoCollections">
        /// If true, then elements of collections will be validated against the metadata.
        /// </param>
        private void ValidateNullOrEmptyArgument(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            Type argumentType,
            object parameterValue,
            bool recurseIntoCollections)
        {
            if (parameterValue == null && argumentType != typeof(bool?))
            {
                if (!parameterMetadata.AllowsNullArgument)
                {
                    bindingTracer.WriteLine("ERROR: Argument cannot be null");

                    ParameterBindingValidationException.ThrowParameterArgumentValidationErrorNullNotAllowed(
                        this.InvocationInfo,
                        GetErrorExtent(parameter),
                        parameterMetadata.Name,
                        argumentType,
                        null);
                }

                return;
            }

            if (argumentType == typeof(string))
            {
                // Since the parameter is of type string, verify that either the argument
                // is not null and not empty or that the parameter can accept null or empty.
                if (parameterValue is not string stringParamValue)
                {
                    Diagnostics.Assert(
                        false,
                        "Type coercion should have already converted the argument value to a string");
                    return;
                }

                if (stringParamValue.Length == 0 && !parameterMetadata.AllowsEmptyStringArgument)
                {
                    bindingTracer.WriteLine("ERROR: Argument cannot be an empty string");

                    ParameterBindingValidationException.ThrowParameterArgumentValidationErrorEmptyStringNotAllowed(
                        this.InvocationInfo,
                        GetErrorExtent(parameter),
                        parameterMetadata.Name,
                        parameterMetadata.Type,
                        parameterValue?.GetType());
                }

                return;
            }

            if (!recurseIntoCollections)
                return;

            switch (parameterMetadata.CollectionTypeInformation.ParameterCollectionType)
            {
                case ParameterCollectionType.IList:
                case ParameterCollectionType.Array:
                case ParameterCollectionType.ICollectionGeneric:
                    break;
                default:
                    // not a recognized collection, no need to recurse
                    return;
            }

            // All these collection types implement IEnumerable
            IEnumerator ienum = LanguagePrimitives.GetEnumerator(parameterValue);
            Diagnostics.Assert(
                ienum != null,
                "Type coercion should have already converted the argument value to an IEnumerator");

            // Ensure that each element abides by the metadata
            bool isEmpty = true;
            Type elementType = parameterMetadata.CollectionTypeInformation.ElementType;
            bool isElementValueType = elementType != null && elementType.IsValueType;

            // Note - we explicitly don't pass the context here because we don't want
            // the overhead of the calls that check for stopping.
            if (ParserOps.MoveNext(null, null, ienum))
            {
                isEmpty = false;
            }

            // If the element of the collection is of value type, then no need to check for null
            // because a value-type value cannot be null.
            if (!isEmpty && !isElementValueType)
            {
                do
                {
                    object element = ParserOps.Current(null, ienum);
                    ValidateNullOrEmptyArgument(
                        parameter,
                        parameterMetadata,
                        parameterMetadata.CollectionTypeInformation.ElementType,
                        element,
                        false);
                } while (ParserOps.MoveNext(null, null, ienum));
            }

            if (isEmpty && !parameterMetadata.AllowsEmptyCollectionArgument)
            {
                bindingTracer.WriteLine("ERROR: Argument cannot be an empty collection");

                string errorId, resourceString;
                if (parameterMetadata.CollectionTypeInformation.ParameterCollectionType == ParameterCollectionType.Array)
                {
                    errorId = "ParameterArgumentValidationErrorEmptyArrayNotAllowed";
                    resourceString = ParameterBinderStrings.ParameterArgumentValidationErrorEmptyArrayNotAllowed;
                }
                else
                {
                    errorId = "ParameterArgumentValidationErrorEmptyCollectionNotAllowed";
                    resourceString = ParameterBinderStrings.ParameterArgumentValidationErrorEmptyCollectionNotAllowed;
                }

                ParameterBindingValidationException.ThrowValidateNullOrEmpty(
                    this.InvocationInfo,
                    GetErrorExtent(parameter),
                    parameterMetadata.Name,
                    parameterMetadata.Type,
                    parameterValue?.GetType(),
                    resourceString,
                    errorId);
            }
        }

        private bool ShouldContinueUncoercedBind(
            CommandParameterInternal parameter,
            CompiledCommandParameter parameterMetadata,
            ParameterBindingFlags flags,
            ref object parameterValue)
        {
            bool isDefaultValue = (flags & ParameterBindingFlags.IsDefaultValue) != 0;
            Type parameterType = parameterMetadata.Type;

            if (parameterValue == null)
            {
                return parameterType == null ||
                       isDefaultValue ||
                       (!parameterType.IsValueType &&
                        parameterType != typeof(string));
            }

            // If the types are not a direct match, or
            // the value type is not a subclass of the parameter type, or
            // the value is an PSObject and the parameter type is not object and
            //     the PSObject.BaseObject type does not match or is not a subclass
            //     of the parameter type, or
            // the value must be encoded into a collection but it is not of the correct element type
            //
            // then return false

            if (parameterType.IsInstanceOfType(parameterValue))
            {
                return true;
            }

            if (parameterValue is PSObject psobj && !psobj.ImmediateBaseObjectIsEmpty)
            {
                // See if the base object is of the same type or
                // as subclass of the parameter

                parameterValue = psobj.BaseObject;

                if (parameterType.IsInstanceOfType(parameterValue))
                {
                    return true;
                }
            }

            // Maybe the parameter type is a collection and the value needs to
            // be encoded

            if (parameterMetadata.CollectionTypeInformation.ParameterCollectionType != ParameterCollectionType.NotCollection)
            {
                // See if the value needs to be encoded in a collection

                bool coercionRequired;
                object encodedValue =
                    _typeCoercer.EncodeCollection(
                        parameter,
                        parameterMetadata.Name,
                        parameterMetadata.CollectionTypeInformation,
                        parameterType,
                        parameterValue,
                        false,
                        out coercionRequired);

                if (encodedValue == null || coercionRequired)
                {
                    // Don't attempt the bind because the
                    // PSObject BaseObject is not of the correct
                    // type for the parameter.
                    return false;
                }

                parameterValue = encodedValue;
                return true;
            }

            return false;
        }

        #endregion Parameter binding

        /// <summary>
        /// The invocation information for the code that is being bound.
        /// </summary>
        private readonly InvocationInfo _invocationInfo;

        internal InvocationInfo InvocationInfo
        {
            get
            {
                return _invocationInfo;
            }
        }

        /// <summary>
        /// The context of the currently running engine.
        /// </summary>
        private readonly ExecutionContext _context;

        internal ExecutionContext Context
        {
            get
            {
                return _context;
            }
        }

        /// <summary>
        /// An instance of InternalCommand that the binder is binding to.
        /// </summary>
        private readonly InternalCommand _command;

        private readonly ParameterTypeCoercer _typeCoercer;

        internal InternalCommand Command
        {
            get
            {
                return _command;
            }
        }

        /// <summary>
        /// The engine APIs that need to be passed the attributes when evaluated.
        /// </summary>
        private readonly EngineIntrinsics _engine;

        private readonly bool _isTranscribing;

        #endregion internal members

        #region Private helpers

        internal static IList GetIList(object value)
        {
            var baseObj = PSObject.Base(value);
            if (baseObj is IList result)
            {
                // Reference comparison to determine if 'value' is a PSObject
                s_tracer.WriteLine(baseObj == value
                                     ? "argument is IList"
                                     : "argument is PSObject with BaseObject as IList");

                return result;
            }

            return null;
        }

        /// <summary>
        /// Gets the script extent used to position binding errors for a parameter argument.
        /// Falls back to <see cref="InvocationInfo.ScriptPosition"/> when argument extent is unavailable.
        /// </summary>
        protected IScriptExtent GetErrorExtent(CommandParameterInternal cpi)
        {
            var result = cpi.ErrorExtent;
            if (result == PositionUtilities.EmptyExtent)
                result = InvocationInfo.ScriptPosition;
            // Can't use this assertion - we don't have useful positions when invoked via PowerShell API
            // Diagnostics.Assert(result != PositionUtilities.EmptyExtent, "We are missing a valid position somewhere");
            return result;
        }

        /// <summary>
        /// Gets the script extent for the parameter name token when reporting parameter-name
        /// resolution errors, with fallback to <see cref="InvocationInfo.ScriptPosition"/>.
        /// </summary>
        protected IScriptExtent GetParameterErrorExtent(CommandParameterInternal cpi)
        {
            var result = cpi.ParameterExtent;
            if (result == PositionUtilities.EmptyExtent)
                result = InvocationInfo.ScriptPosition;
            // Can't use this assertion - we don't have useful positions when invoked via PowerShell API
            // Diagnostics.Assert(result != PositionUtilities.EmptyExtent, "We are missing a valid position somewhere");
            return result;
        }

        #endregion private helpers
    }

    /// <summary>
    /// Represents an unbound parameter object in the engine. It's similar to
    /// AutomationNull.Value however AutomationNull.Value as a parameter value
    /// is used to say "use the default value for this object" whereas UnboundParameter
    /// says "this parameter is unbound, use the default only if the target type
    /// supports permits this."
    /// </summary>
    /// <remarks>It's a singleton class. Sealed to prevent subclassing</remarks>
    internal sealed class UnboundParameter
    {
        #region ctor

        // Private constructor
        private UnboundParameter() { }

        #endregion ctor

        #region private_members

        // Private member for Value.

        #endregion private_members

        #region public_property

        /// <summary>
        /// Represents an object of the same class (singleton class).
        /// </summary>
        internal static object Value { get; } = new object();

        #endregion public_property
    }

    // This class is a thin wrapper around Dictionary, but adds a member BoundPositionally.
    // $PSBoundParameters used to be a PSObject with an instance member, but that was quite
    // slow for a relatively common case, this class should work identically, except maybe
    // if somebody depends on the typename being the same.
    internal sealed class PSBoundParametersDictionary : Dictionary<string, object>
    {
        internal PSBoundParametersDictionary()
            : base(StringComparer.OrdinalIgnoreCase)
        {
            BoundPositionally = new List<string>();
            ImplicitUsingParameters = s_emptyUsingParameters;
        }

        private static readonly IDictionary s_emptyUsingParameters = new ReadOnlyDictionary<object, object>(new Dictionary<object, object>());

        public List<string> BoundPositionally { get; }

        internal IDictionary ImplicitUsingParameters { get; set; }
    }

    internal sealed class CommandLineParameters
    {
        private readonly PSBoundParametersDictionary _dictionary = new PSBoundParametersDictionary();

        internal bool ContainsKey(string name)
        {
            Dbg.Assert(!string.IsNullOrEmpty(name), "parameter names should not be empty");
            return _dictionary.ContainsKey(name);
        }

        internal void Add(string name, object value)
        {
            Dbg.Assert(!string.IsNullOrEmpty(name), "parameter names should not be empty");
            _dictionary[name] = value;
        }

        internal void MarkAsBoundPositionally(string name)
        {
            Dbg.Assert(!string.IsNullOrEmpty(name), "parameter names should not be empty");
            _dictionary.BoundPositionally.Add(name);
        }

        internal void SetPSBoundParametersVariable(ExecutionContext context)
        {
            Dbg.Assert(context != null, "caller should verify that context != null");

            context.SetVariable(SpecialVariables.PSBoundParametersVarPath, _dictionary);
        }

        internal void SetImplicitUsingParameters(object obj)
        {
            _dictionary.ImplicitUsingParameters = PSObject.Base(obj) as IDictionary;
            if (_dictionary.ImplicitUsingParameters == null)
            {
                // Handle downlevel V4 case where using parameters are passed as an array list.
                IList implicitArrayUsingParameters = PSObject.Base(obj) as IList;
                if ((implicitArrayUsingParameters != null) && (implicitArrayUsingParameters.Count > 0))
                {
                    // Convert array to hash table.
                    _dictionary.ImplicitUsingParameters = new Hashtable();
                    for (int index = 0; index < implicitArrayUsingParameters.Count; index++)
                    {
                        _dictionary.ImplicitUsingParameters.Add(index, implicitArrayUsingParameters[index]);
                    }
                }
            }
        }

        internal IDictionary GetImplicitUsingParameters()
        {
            return _dictionary.ImplicitUsingParameters;
        }

        internal object GetValueToBindToPSBoundParameters()
        {
            return _dictionary;
        }

        internal void UpdateInvocationInfo(InvocationInfo invocationInfo)
        {
            Dbg.Assert(invocationInfo != null, "caller should verify that invocationInfo != null");
            invocationInfo.BoundParameters = _dictionary;
        }

        internal HashSet<string> CopyBoundPositionalParameters()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (string item in _dictionary.BoundPositionally)
            {
                result.Add(item);
            }

            return result;
        }
    }
}
