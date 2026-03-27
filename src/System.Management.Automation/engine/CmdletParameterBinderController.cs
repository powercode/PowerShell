// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Diagnostics;
using System.Management.Automation.Runspaces;
using System.Runtime.ExceptionServices;
using System.Text;

namespace System.Management.Automation
{
    /// <summary>
    /// This is the interface between the CommandProcessor and the various
    /// parameter binders required to bind parameters to a cmdlet.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal class CmdletParameterBinderController : ParameterBinderController, IParameterBindingContext, IDefaultParameterBindingContext, IMandatoryParameterPrompterContext
    {
        #region tracer

        [TraceSource("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).")]
        private static readonly PSTraceSource s_tracer = PSTraceSource.GetTracer("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).");

        #endregion tracer

        #region ctor

        /// <summary>
        /// Initializes the cmdlet parameter binder controller for
        /// the specified cmdlet and engine context.
        /// </summary>
        /// <param name="cmdlet">
        /// The cmdlet that the parameters will be bound to.
        /// </param>
        /// <param name="commandMetadata">
        /// The metadata about the cmdlet.
        /// </param>
        /// <param name="parameterBinder">
        /// The default parameter binder to use.
        /// </param>
        internal CmdletParameterBinderController(
            Cmdlet cmdlet,
            CommandMetadata commandMetadata,
            ParameterBinderBase parameterBinder)
            : base(
                cmdlet.MyInvocation,
                cmdlet.Context,
                parameterBinder)
        {
            if (cmdlet == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(cmdlet));
            }

            if (commandMetadata == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(commandMetadata));
            }

            this.Command = cmdlet;
            _commandRuntime = (MshCommandRuntime)cmdlet.CommandRuntime;
            _commandMetadata = commandMetadata;

            _commonParametersBinder = new ReflectionParameterBinder(
                new CommonParameters(_commandRuntime),
                this.Command,
                this.CommandLineParameters);

            _shouldProcessParameterBinder = new ReflectionParameterBinder(
                new ShouldProcessParameters(_commandRuntime),
                this.Command,
                this.CommandLineParameters);

            _pagingParameterBinder = new ReflectionParameterBinder(
                new PagingParameters(_commandRuntime),
                this.Command,
                this.CommandLineParameters);

            _transactionParameterBinder = new ReflectionParameterBinder(
                new TransactionParameters(_commandRuntime),
                this.Command,
                this.CommandLineParameters);

            // Add the static parameter metadata to the bindable parameters
            // And add them to the unbound parameters list

            if (commandMetadata.ImplementsDynamicParameters)
            {
                // ReplaceMetadata makes a copy for us, so we can use that collection as is.
                this.UnboundParameters = this.BindableParameters.ReplaceMetadata(commandMetadata.StaticCommandParameterMetadata);
            }
            else
            {
                _bindableParameters = commandMetadata.StaticCommandParameterMetadata;

                // Must make a copy of the list because we'll modify it.
                this.UnboundParameters = new List<MergedCompiledCommandParameter>(_bindableParameters.BindableParameters.Values);
            }

            ParameterSetResolver = new System.Management.Automation.ParameterSetResolver(
                commandMetadata: _commandMetadata,
                bindableParameters: this.BindableParameters,
                context: this);

            _defaultParameterValueBinder = new DefaultParameterValueBinder(
                commandMetadata: _commandMetadata,
                commandRuntime: _commandRuntime,
                context: cmdlet.Context,
                bindableParameters: this.BindableParameters,
                bindingContext: this);

            _mandatoryParameterPrompter = new MandatoryParameterPrompter(
                parameterSetResolver: ParameterSetResolver,
                context: this.Context,
                command: this.Command,
                bindingContext: this);
        }

        #endregion ctor

        private string DebuggerDisplayValue
        {
            get
            {
                string commandName = InvocationInfo?.MyCommand?.Name ?? "(unknown)";
                uint setFlag = ParameterSetResolver?.CurrentParameterSetFlag ?? 0;
                return $"CmdletBinder: {commandName}, ParamSet=0x{setFlag:X8}";
            }
        }

        ICollection<MergedCompiledCommandParameter> IParameterBindingContext.UnboundParameters => UnboundParameters;

        Dictionary<string, MergedCompiledCommandParameter> IParameterBindingContext.BoundParameters => BoundParameters;

        InvocationInfo IParameterBindingContext.InvocationInfo => Command.MyInvocation;

        void IParameterBindingContext.SetParameterSetName(string parameterSetName)
            => Command.SetParameterSetName(parameterSetName);

        void IParameterBindingContext.ThrowBindingException(ParameterBindingException exception)
            => ThrowOrElaborateBindingException(exception);

        bool IDefaultParameterBindingContext.DispatchBindToSubBinder(
            uint validParameterSetFlag,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
            => DispatchBindToSubBinder(validParameterSetFlag, argument, parameter, flags);

        Dictionary<string, CommandParameterInternal> IDefaultParameterBindingContext.BoundArguments
            => BoundArguments;

        Dictionary<string, MergedCompiledCommandParameter> IDefaultParameterBindingContext.BoundParameters
            => BoundParameters;

        Collection<string> IDefaultParameterBindingContext.BoundDefaultParameters
            => BoundDefaultParameters;

        bool IMandatoryParameterPrompterContext.ResolveAndBindNamedParameter(
            CommandParameterInternal argument,
            ParameterBindingFlags flags)
            => ResolveAndBindNamedParameter(argument, flags);

        HashSet<string> IDefaultParameterBindingContext.CopyBoundPositionalParameters()
            => DefaultParameterBinder.CommandLineParameters.CopyBoundPositionalParameters();

        #region helper_methods

        /// <summary>
        /// Binds the specified command-line parameters to the target.
        /// </summary>
        /// <remarks>
        /// The command-line binding orchestration executes these high-level phases in order:
        ///
        /// 1. Run non-validating command-line binding (<see cref="BindCommandLineParametersNoValidation(Collection{CommandParameterInternal})"/>).
        /// 2. Determine whether pipeline input is expected for this invocation.
        /// 3. Validate and narrow candidate parameter sets for strict execution mode.
        /// 4. Re-apply default parameter binding for mandatory-check scenarios when a single set is selected.
        /// 5. If pipeline input is expected and multiple sets remain, filter out sets that cannot take pipeline input.
        /// 6. Handle unbound mandatory parameters (prompt or throw as appropriate for command visibility and mode).
        /// 7. Bind remaining unbound script parameters for script binder scenarios.
        /// 8. If no more pipeline input is expected, verify that a single parameter set is selected.
        /// 9. Persist pre-pipeline parameter set flags for per-input-object pipeline rebinding.
        /// </remarks>
        /// <param name="arguments">
        /// Parameters to the command.
        /// </param>
        /// <exception cref="ParameterBindingException">
        /// If any parameters fail to bind,
        /// or
        /// If any mandatory parameters are missing.
        /// </exception>
        /// <exception cref="MetadataException">
        /// If there is an error generating the metadata for dynamic parameters.
        /// </exception>
        internal void BindCommandLineParameters(Collection<CommandParameterInternal> arguments)
        {
            s_tracer.WriteLine("Argument count: {0}", arguments.Count);

            BindCommandLineParametersNoValidation(arguments);

            // Is pipeline input expected?
            bool isPipelineInputExpected = !(_commandRuntime.IsClosed && _commandRuntime.InputPipe.Empty);

            int validParameterSetCount;

            if (!isPipelineInputExpected)
            {
                // Since pipeline input is not expected, ensure that we have a single
                // parameter set and that all the mandatory
                // parameters for the working parameter set are specified, or prompt

                validParameterSetCount = ParameterSetResolver.ValidateParameterSets(false, true, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
            }
            else
            {
                // Use ValidateParameterSets to get the number of valid parameter
                // sets.
                validParameterSetCount = ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
            }

            // If the parameter set is determined and the default parameters are not used
            // we try the default parameter binding again because it may contain some mandatory
            // parameters
            if (validParameterSetCount == 1 && !DefaultParameterBindingInUse)
            {
                if (_defaultParameterValueBinder.ApplyDefaultParameterBinding(
                    "Mandatory Checking",
                    isDynamic: false,
                    currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
                {
                    DefaultParameterBindingInUse = true;
                }
            }

            // If there are multiple valid parameter sets and we are expecting pipeline inputs,
            // we should filter out those parameter sets that cannot take pipeline inputs anymore.
            if (validParameterSetCount > 1 && isPipelineInputExpected)
            {
                uint filteredValidParameterSetFlags = ParameterSetResolver.FilterParameterSetsTakingNoPipelineInput(_delayBindScriptBlocks.Keys);
                if (filteredValidParameterSetFlags != ParameterSetResolver.CurrentParameterSetFlag)
                {
                    ParameterSetResolver.CurrentParameterSetFlag = filteredValidParameterSetFlags;
                    // The valid parameter set flag is narrowed down, we get the new validParameterSetCount
                    validParameterSetCount = ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
                }
            }

            using (ParameterBinderBase.bindingTracer.TraceScope(
                "MANDATORY PARAMETER CHECK on cmdlet [{0}]",
                _commandMetadata.Name))
            {
                try
                {
                    // The missingMandatoryParameters out parameter is used for error reporting when binding from the pipeline.
                    // We're not binding from the pipeline here, and if a mandatory non-pipeline parameter is missing, it will
                    // be prompted for, or an exception will be raised, so we can ignore the missingMandatoryParameters out parameter.
                    Collection<MergedCompiledCommandParameter> missingMandatoryParameters;

                    // We shouldn't prompt for mandatory parameters if this command is private.
                    bool promptForMandatoryParameters = (Command.CommandInfo.Visibility == SessionStateEntryVisibility.Public);
                    _mandatoryParameterPrompter.HandleUnboundMandatoryParameters(validParameterSetCount, true, promptForMandatoryParameters, isPipelineInputExpected, out missingMandatoryParameters);

                    if (DefaultParameterBinder is ScriptParameterBinder)
                    {
                        BindUnboundScriptParameters();
                    }
                }
                catch (ParameterBindingException pbex)
                {
                    ThrowOrElaborateBindingException(pbex);
                }
            }

            // If there is no more expected input, ensure there is a single
            // parameter set selected

            if (!isPipelineInputExpected)
            {
                ParameterSetResolver.VerifyParameterSetSelected();
            }

            // Set the prepipeline parameter set flags so that they can be restored
            // between each pipeline object.

            ParameterSetResolver.PrePipelineProcessingParameterSetFlags = ParameterSetResolver.CurrentParameterSetFlag;
        }

        /// <summary>
        /// Binds the unbound arguments to parameters but does not
        /// perform mandatory parameter validation or parameter set validation.
        /// </summary>
        /// <remarks>
        /// The non-validating binding pipeline executes these phases in order:
        ///
        /// 1. Initialize unbound arguments from command-line tokens.
        /// 2. Parse $PSDefaultParameterValues for this command.
        /// 3. Re-pair parameter names with argument values (<see cref="ParameterBinderController.ReparseUnboundArguments"/>).
        /// 4. Bind named parameters with exact name-to-parameter matching.
        /// 5. Bind positional parameters with the 4-pass algorithm:
        ///    a. Default parameter set, no type coercion.
        ///    b. Other parameter sets, no type coercion.
        ///    c. Default parameter set, with type coercion.
        ///    d. Other parameter sets, with type coercion.
        /// 6. Apply $PSDefaultParameterValues after positional binding.
        /// 7. Validate that at least one parameter set remains resolvable.
        /// 8. Handle dynamic parameters and re-run matching with expanded metadata.
        /// 9. Re-apply $PSDefaultParameterValues after dynamic binding.
        /// 10. Bind ValueFromRemainingArguments parameters.
        /// 11. Verify all command-line arguments were consumed.
        /// </remarks>
        internal void BindCommandLineParametersNoValidation(Collection<CommandParameterInternal> arguments)
        {
            var psCompiledScriptCmdlet = this.Command as PSScriptCmdlet;
            psCompiledScriptCmdlet?.PrepareForBinding(this.CommandLineParameters);

            InitUnboundArguments(arguments);
            CommandMetadata cmdletMetadata = _commandMetadata;
            // Parse $PSDefaultParameterValues to get all valid <parameter, value> pairs
            _defaultParameterValueBinder.ResetForNewBinding();
            _defaultParameterValueBinder.GetDefaultParameterValuePairs(true);
            // Set to false at the beginning
            DefaultParameterBindingInUse = false;
            // Clear the bound default parameters at the beginning
            BoundDefaultParameters.Clear();

            // Reparse the arguments based on the merged metadata
            ReparseUnboundArguments();

            using (ParameterBinderBase.bindingTracer.TraceScope(
                "BIND NAMED cmd line args [{0}]",
                _commandMetadata.Name))
            {
                // Bind the actual arguments
                UnboundArguments = BindNamedParameters(ParameterSetResolver.CurrentParameterSetFlag, this.UnboundArguments);
            }

            ParameterBindingException reportedBindingException;
            ParameterBindingException currentBindingException;

            using (ParameterBinderBase.bindingTracer.TraceScope(
                "BIND POSITIONAL cmd line args [{0}]",
                _commandMetadata.Name))
            {
                // Now that we know the parameter set, bind the positional parameters
                UnboundArguments =
                    BindPositionalParameters(
                        UnboundArguments,
                        ParameterSetResolver.CurrentParameterSetFlag,
                        cmdletMetadata.DefaultParameterSetFlag,
                        out currentBindingException);

                reportedBindingException = currentBindingException;
            }

            // Try applying the default parameter binding after POSITIONAL BIND so that the default parameter
            // values can influence the parameter set selection earlier than the default parameter set.
            if (_defaultParameterValueBinder.ApplyDefaultParameterBinding(
                "POSITIONAL BIND",
                isDynamic: false,
                currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
            {
                DefaultParameterBindingInUse = true;
            }

            // We need to make sure there is at least one valid parameter set. Its
            // OK to allow more than one as long as one of them takes pipeline input.
            ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);

            // Always get the dynamic parameters as there may be mandatory parameters there

            // Now try binding the dynamic parameters
            HandleCommandLineDynamicParameters(out currentBindingException);

            // Try binding the default parameters again. After dynamic binding, new parameter metadata are
            // included, so it's possible a previously unsuccessful binding will succeed.
            if (_defaultParameterValueBinder.ApplyDefaultParameterBinding(
                "DYNAMIC BIND",
                isDynamic: true,
                currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
            {
                DefaultParameterBindingInUse = true;
            }

            // If this generated an exception (but we didn't have one from the non-dynamic
            // parameters, report on this one.
            reportedBindingException ??= currentBindingException;

            // If the cmdlet implements a ValueFromRemainingArguments parameter (VarArgs)
            // bind the unbound arguments to that parameter.
            HandleRemainingArguments();

            VerifyArgumentsProcessed(reportedBindingException);
        }

        internal IDictionary DefaultParameterValues
        {
            get => _defaultParameterValueBinder.DefaultParameterValues;
            set => _defaultParameterValueBinder.DefaultParameterValues = value;
        }

        /// <summary>
        /// Verify if all arguments from the command line are bound.
        /// </summary>
        /// <param name="originalBindingException">
        /// Previous binding exceptions that possibly causes the failure
        /// </param>
        private void VerifyArgumentsProcessed(ParameterBindingException originalBindingException)
        {
            // Now verify that all the arguments that were passed in were processed.

            if (UnboundArguments.Count > 0)
            {
                ParameterBindingException bindingException;
                CommandParameterInternal parameter = UnboundArguments[0];

                // Get the argument type that was specified

                Type specifiedType = null;
                object argumentValue = parameter.ArgumentValue;
                if (argumentValue != null && argumentValue != UnboundParameter.Value)
                {
                    specifiedType = argumentValue.GetType();
                }

                if (parameter.ParameterNameSpecified)
                {
                    bindingException = ParameterBindingException.NewNamedParameterNotFound(
                        this.Command.MyInvocation,
                        GetParameterErrorExtent(parameter),
                        parameter.ParameterName,
                        specifiedType);
                }
                else
                {
                    // If this was a positional parameter, and we have the original exception,
                    // report on the original error
                    if (originalBindingException != null)
                    {
                        bindingException = originalBindingException;
                    }
                    // Otherwise, give a generic error.
                    else
                    {
                        string argument = StringLiterals.DollarNull;
                        if (parameter.ArgumentValue != null)
                        {
                            try
                            {
                                argument = parameter.ArgumentValue.ToString();
                            }
                            catch (Exception e)
                            {
                                bindingException =
                                    ParameterBindingArgumentTransformationException.NewParameterArgumentTransformationErrorMessageOnly(
                                        e,
                                        this.InvocationInfo,
                                        null,
                                        null,
                                        null,
                                        parameter.ArgumentValue.GetType(),
                                        e.Message);

                                ThrowOrElaborateBindingException(bindingException);
                            }
                        }

                        bindingException = ParameterBindingException.NewPositionalParameterNotFound(
                            this.Command.MyInvocation,
                            null,
                            argument,
                            specifiedType);
                    }
                }

                ThrowOrElaborateBindingException(bindingException);
            }
        }

        /// <summary>
        /// Restores the specified parameter to the original value.
        /// </summary>
        /// <param name="argumentToBind">
        /// The argument containing the value to restore.
        /// </param>
        /// <param name="parameter">
        /// The metadata for the parameter to restore.
        /// </param>
        /// <returns>
        /// True if the parameter was restored correctly, or false otherwise.
        /// </returns>
        private bool RestoreParameter(CommandParameterInternal argumentToBind, MergedCompiledCommandParameter parameter)
        {
            GetBinderForParameter(parameter)?.StoreParameterValue(argumentToBind.ParameterName, argumentToBind.ArgumentValue, parameter.Parameter);

            return true;
        }

        /// <summary>
        /// Validate the given named parameter against the specified parameter set,
        /// and then bind the argument to the parameter.
        /// </summary>
        protected override void BindNamedParameter(
            uint parameterSets,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter)
        {
            if ((parameter.Parameter.ParameterSetFlags & parameterSets) == 0 &&
                !parameter.Parameter.IsInAllSets)
            {
                string parameterSetName = BindableParameters.GetParameterSetName(parameterSets);

                ParameterBindingException bindingException =
                    ParameterBindingException.NewParameterNotInParameterSet(
                        this.Command.MyInvocation,
                        argument.ParameterName,
                        parameterSetName);

                // Might be caused by default parameter binding
                ThrowOrElaborateBindingException(bindingException);
            }

            try
            {
                DispatchBindToSubBinder(parameterSets, argument, parameter,
                    ParameterBindingFlags.ShouldCoerceType | ParameterBindingFlags.DelayBindScriptBlock);
            }
            catch (ParameterBindingException pbex)
            {
                ThrowOrElaborateBindingException(pbex);
            }
        }

        /// <summary>
        /// Determines if a ScriptBlock can be bound directly to the type of the specified parameter.
        /// </summary>
        /// <param name="parameter">
        /// The metadata of the parameter to check the type of.
        /// </param>
        /// <returns>
        /// true if the parameter type is Object, ScriptBlock, derived from ScriptBlock, a
        /// collection of ScriptBlocks, a collection of Objects, or a collection of types derived from
        /// ScriptBlock.
        /// False otherwise.
        /// </returns>
        private static bool IsParameterScriptBlockBindable(MergedCompiledCommandParameter parameter)
        {
            bool result = IsScriptBlockCompatible(parameter.Parameter.Type);

            if (!result)
            {
                ParameterCollectionTypeInformation collectionTypeInfo = parameter.Parameter.CollectionTypeInformation;
                if (collectionTypeInfo.ParameterCollectionType != ParameterCollectionType.NotCollection)
                {
                    result = IsScriptBlockCompatible(collectionTypeInfo.ElementType);
                }
            }

            s_tracer.WriteLine("IsParameterScriptBlockBindable: result = {0}", result);
            return result;

            static bool IsScriptBlockCompatible(Type type) =>
                type == typeof(object) || typeof(ScriptBlock).IsAssignableFrom(type);
        }

        /// <summary>
        /// Binds the specified argument to the specified parameter using the appropriate
        /// parameter binder. If the argument is of type ScriptBlock and the parameter takes
        /// pipeline input, then the ScriptBlock is saved off in the delay-bind ScriptBlock
        /// container for further processing of pipeline input and is not bound as the argument
        /// to the parameter.
        /// </summary>
        /// <param name="parameterSets">
        /// The parameter set used to bind the arguments.
        /// </param>
        /// <param name="argument">
        /// The argument to be bound.
        /// </param>
        /// <param name="parameter">
        /// The metadata for the parameter to bind the argument to.
        /// </param>
        /// <param name="flags">
        /// Flags for type coercion, validation, and script block binding.
        ///
        /// ParameterBindingFlags.DelayBindScriptBlock:
        /// If set, arguments that are of type ScriptBlock where the parameter is not of type ScriptBlock,
        /// Object, or PSObject will be stored for execution during pipeline input and not bound as
        /// an argument to the parameter.
        /// </param>
        /// <returns>
        /// True if the parameter was successfully bound. False if <paramref name="flags"/>
        /// has the flag <see cref="ParameterBindingFlags.ShouldCoerceType"/> set and the type does not match the parameter type.
        /// </returns>
        internal override bool DispatchBindToSubBinder(
            uint parameterSets,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
        {
            // Now we need to check to see if the argument value is
            // a ScriptBlock.  If it is and the parameter type is
            // not ScriptBlock and not Object, then we need to delay
            // binding until a pipeline object is provided to invoke
            // the ScriptBlock.

            // Note: we haven't yet determined that only a single parameter
            // set is valid, so we have to take a best guess on pipeline input
            // based on the current valid parameter sets.

            bool continueWithBinding = true;

            if ((flags & ParameterBindingFlags.DelayBindScriptBlock) != 0 &&
                parameter.Parameter.DoesParameterSetTakePipelineInput(parameterSets) &&
                argument.ArgumentSpecified)
            {
                object argumentValue = argument.ArgumentValue;
                if ((argumentValue is ScriptBlock || argumentValue is DelayedScriptBlockArgument) &&
                    !IsParameterScriptBlockBindable(parameter))
                {
                    // Now check to see if the command expects to have pipeline input.
                    // If not, we should throw an exception now to inform the
                    // user with more information than they would get if it was
                    // considered an unbound mandatory parameter.

                    if (_commandRuntime.IsClosed && _commandRuntime.InputPipe.Empty)
                    {
                        ParameterBindingException.ThrowScriptBlockArgumentNoInput(
                            this.Command.MyInvocation,
                            GetErrorExtent(argument),
                            parameter.Parameter.Name,
                            parameter.Parameter.Type);
                    }

                    ParameterBinderBase.bindingTracer.WriteLine(
                        "Adding ScriptBlock to delay-bind list for parameter '{0}'",
                        parameter.Parameter.Name);

                    // We need to delay binding of this argument to the parameter

                    DelayedScriptBlockArgument delayedArg = argumentValue as DelayedScriptBlockArgument ??
                                                            new DelayedScriptBlockArgument { _argument = argument, _parameterBinder = this };
                    if (!_delayBindScriptBlocks.ContainsKey(parameter))
                    {
                        _delayBindScriptBlocks.Add(parameter, delayedArg);
                    }

                    // We treat the parameter as bound, but really the
                    // script block gets run for each pipeline object and
                    // the result is bound.

                    if (parameter.Parameter.ParameterSetFlags != 0)
                    {
                        ParameterSetResolver.NarrowByParameterSetFlags(parameter.Parameter.ParameterSetFlags);
                    }

                    UnboundParameters.Remove(parameter);

                    BoundParameters[parameter.Parameter.Name] = parameter;
                    BoundArguments[parameter.Parameter.Name] = argument;

                    if (DefaultParameterBinder.RecordBoundParameters &&
                        !DefaultParameterBinder.CommandLineParameters.ContainsKey(parameter.Parameter.Name))
                    {
                        DefaultParameterBinder.CommandLineParameters.Add(parameter.Parameter.Name, delayedArg);
                    }

                    continueWithBinding = false;
                }
            }

            bool result = false;
            if (continueWithBinding)
            {
                try
                {
                    result = BindToAssociatedBinder(argument, parameter, flags);
                }
                catch (Exception e)
                {
                    bool rethrow = true;
                    if ((flags & ParameterBindingFlags.ShouldCoerceType) == 0)
                    {
                        // Attributes are used to do type coercion and result in various exceptions.
                        // We assume that if we aren't trying to do type coercion, we should avoid
                        // propagating type conversion exceptions.
                        while (e != null)
                        {
                            if (e is PSInvalidCastException)
                            {
                                rethrow = false;
                                break;
                            }

                            e = e.InnerException;
                        }
                    }

                    if (rethrow)
                    {
                        throw;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Binds the specified argument to the specified parameter using the appropriate
        /// parameter binder.
        /// </summary>
        /// <param name="argument">
        /// The argument to be bound.
        /// </param>
        /// <param name="parameter">
        /// The metadata for the parameter to bind the argument to.
        /// </param>
        /// <param name="flags">
        /// Flags for type coercion and validation.
        /// </param>
        /// <returns>
        /// True if the parameter was successfully bound. False if <paramref name="flags"/>
        /// has the flag <see cref="ParameterBindingFlags.ShouldCoerceType"/> set and the type does not match the parameter type.
        /// </returns>
        private bool BindToAssociatedBinder(
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
        {
            bool result = false;

            switch (parameter.BinderAssociation)
            {
                case ParameterBinderAssociation.DeclaredFormalParameters:
                    result =
                        DefaultParameterBinder.CoerceValidateAndBind(
                            argument,
                            parameter.Parameter,
                            flags);
                    break;

                case ParameterBinderAssociation.CommonParameters:
                    result =
                        _commonParametersBinder.CoerceValidateAndBind(
                            argument,
                            parameter.Parameter,
                            flags);
                    break;

                case ParameterBinderAssociation.ShouldProcessParameters:
                    Diagnostics.Assert(
                        _commandMetadata.SupportsShouldProcess,
                        "The metadata for the ShouldProcessParameters should only be available if the command supports ShouldProcess");

                    result =
                        _shouldProcessParameterBinder.CoerceValidateAndBind(
                            argument,
                            parameter.Parameter,
                            flags);
                    break;

                case ParameterBinderAssociation.PagingParameters:
                    Diagnostics.Assert(
                        _commandMetadata.SupportsPaging,
                        "The metadata for the PagingParameters should only be available if the command supports paging");

                    result =
                        _pagingParameterBinder.CoerceValidateAndBind(
                            argument,
                            parameter.Parameter,
                            flags);
                    break;

                case ParameterBinderAssociation.TransactionParameters:
                    Diagnostics.Assert(
                        _commandMetadata.SupportsTransactions,
                        "The metadata for the TransactionsParameters should only be available if the command supports transactions");

                    result =
                        _transactionParameterBinder.CoerceValidateAndBind(
                            argument,
                            parameter.Parameter,
                            flags);
                    break;

                case ParameterBinderAssociation.DynamicParameters:
                    Diagnostics.Assert(
                        _commandMetadata.ImplementsDynamicParameters,
                        "The metadata for the dynamic parameters should only be available if the command supports IDynamicParameters");

                    if (_dynamicParameterBinder != null)
                    {
                        result =
                            _dynamicParameterBinder.CoerceValidateAndBind(
                                argument,
                                parameter.Parameter,
                                flags);
                    }

                    break;
            }

            if (result && ((flags & ParameterBindingFlags.IsDefaultValue) == 0))
            {
                // Update the current valid parameter set flags

                if (parameter.Parameter.ParameterSetFlags != 0)
                {
                    ParameterSetResolver.NarrowByParameterSetFlags(parameter.Parameter.ParameterSetFlags);
                }

                UnboundParameters.Remove(parameter);

                if (!BoundParameters.ContainsKey(parameter.Parameter.Name))
                {
                    BoundParameters.Add(parameter.Parameter.Name, parameter);
                }

                if (!BoundArguments.ContainsKey(parameter.Parameter.Name))
                {
                    BoundArguments.Add(parameter.Parameter.Name, argument);
                }

                if (parameter.Parameter.ObsoleteAttribute != null &&
                    (flags & ParameterBindingFlags.IsDefaultValue) == 0 &&
                    !BoundObsoleteParameterNames.Contains(parameter.Parameter.Name))
                {
                    string obsoleteWarning = string.Format(
                        CultureInfo.InvariantCulture,
                        ParameterBinderStrings.UseOfDeprecatedParameterWarning,
                        parameter.Parameter.Name,
                        parameter.Parameter.ObsoleteAttribute.Message);
                    var warningRecord = new WarningRecord(ParameterBinderBase.FQIDParameterObsolete, obsoleteWarning);

                    BoundObsoleteParameterNames.Add(parameter.Parameter.Name);

                    ObsoleteParameterWarningList ??= new List<WarningRecord>();

                    ObsoleteParameterWarningList.Add(warningRecord);
                }
            }

            return result;
        }

        /// <summary>
        /// Binds the remaining arguments to an unbound ValueFromRemainingArguments parameter (Varargs)
        /// </summary>
        /// <exception cref="ParameterBindingException">
        /// If there was an error binding the arguments to the parameters.
        /// </exception>
        private void HandleRemainingArguments()
        {
            if (UnboundArguments.Count > 0)
            {
                // Find the parameters that take the remaining args, if there are more
                // than one and the parameter set has not been defined, this is an error

                MergedCompiledCommandParameter varargsParameter = null;

                foreach (MergedCompiledCommandParameter parameter in UnboundParameters)
                {
                    ParameterSetSpecificMetadata parameterSetData = parameter.Parameter.GetParameterSetData(ParameterSetResolver.CurrentParameterSetFlag);

                    if (parameterSetData == null)
                    {
                        continue;
                    }

                    // If the parameter takes the remaining arguments, bind them.

                    if (parameterSetData.ValueFromRemainingArguments)
                    {
                        if (varargsParameter != null)
                        {
                            ParameterBindingException bindingException =
                                ParameterBindingException.NewAmbiguousParameterSet(this.Command.MyInvocation);

                            // Might be caused by the default parameter binding
                            ThrowOrElaborateBindingException(bindingException);
                        }

                        varargsParameter = parameter;
                    }
                }

                if (varargsParameter != null)
                {
                    using (ParameterBinderBase.bindingTracer.TraceScope(
                        "BIND REMAININGARGUMENTS cmd line args to param: [{0}]",
                        varargsParameter.Parameter.Name))
                    {
                        // Accumulate the unbound arguments in to an list and then bind it to the parameter

                        List<object> valueFromRemainingArguments = new List<object>();

                        foreach (CommandParameterInternal argument in UnboundArguments)
                        {
                            if (argument.ParameterNameSpecified)
                            {
                                Diagnostics.Assert(!string.IsNullOrEmpty(argument.ParameterText), "Don't add a null argument");
                                valueFromRemainingArguments.Add(argument.ParameterText);
                            }

                            if (argument.ArgumentSpecified)
                            {
                                object argumentValue = argument.ArgumentValue;
                                if (argumentValue != AutomationNull.Value && argumentValue != UnboundParameter.Value)
                                {
                                    valueFromRemainingArguments.Add(argumentValue);
                                }
                            }
                        }

                        // If there are multiple arguments, it's not clear how best to represent the extent as the extent
                        // may be disjoint, as in 'echo a -verbose b', we have 'a' and 'b' in UnboundArguments.
                        var argumentAst = UnboundArguments.Count == 1 ? UnboundArguments[0].ArgumentAst : null;
                        var cpi = CommandParameterInternal.CreateParameterWithArgument(
                            /*parameterAst*/null, varargsParameter.Parameter.Name, "-" + varargsParameter.Parameter.Name + ":",
                            argumentAst, valueFromRemainingArguments, false);

                        // To make all of the following work similarly (the first is handled elsewhere, but second and third are
                        // handled here):
                        //     Set-ClusterOwnerNode -Owners foo,bar
                        //     Set-ClusterOwnerNode foo bar
                        //     Set-ClusterOwnerNode foo,bar
                        // we unwrap our List, but only if there is a single argument which is a collection.
                        if (valueFromRemainingArguments.Count == 1 && LanguagePrimitives.IsObjectEnumerable(valueFromRemainingArguments[0]))
                        {
                            cpi.SetArgumentValue(UnboundArguments[0].ArgumentAst, valueFromRemainingArguments[0]);
                        }

                        try
                        {
                            BindToAssociatedBinder(cpi, varargsParameter, ParameterBindingFlags.ShouldCoerceType);
                        }
                        catch (ParameterBindingException pbex)
                        {
                            ThrowOrElaborateBindingException(pbex);
                        }

                        UnboundArguments.Clear();
                    }
                }
            }
        }

        /// <summary>
        /// Determines if the cmdlet supports dynamic parameters. If it does,
        /// the dynamic parameter bindable object is retrieved and the unbound
        /// arguments are bound to it.
        /// </summary>
        /// <param name="outgoingBindingException">
        /// Returns the underlying parameter binding exception if any was generated.
        /// </param>
        /// <exception cref="MetadataException">
        /// If there was an error compiling the parameter metadata.
        /// </exception>
        /// <exception cref="ParameterBindingException">
        /// If there was an error binding the arguments to the parameters.
        /// </exception>
        private void HandleCommandLineDynamicParameters(out ParameterBindingException outgoingBindingException)
        {
            outgoingBindingException = null;

            if (_commandMetadata.ImplementsDynamicParameters)
            {
                using (ParameterBinderBase.bindingTracer.TraceScope(
                    "BIND cmd line args to DYNAMIC parameters."))
                {
                    s_tracer.WriteLine("The Cmdlet supports the dynamic parameter interface");

                    if (this.Command is IDynamicParameters dynamicParameterCmdlet)
                    {
                        if (_dynamicParameterBinder == null)
                        {
                            s_tracer.WriteLine("Getting the bindable object from the Cmdlet");

                            // Now get the dynamic parameter bindable object.
                            object dynamicParamBindableObject;

                            try
                            {
                                dynamicParamBindableObject = dynamicParameterCmdlet.GetDynamicParameters();
                            }
                            catch (Exception e) // Catch-all OK, this is a third-party callout
                            {
                                if (e is ProviderInvocationException)
                                {
                                    throw;
                                }

                                ParameterBindingException bindingException =
                                    ParameterBindingException.NewGetDynamicParametersException(
                                        e,
                                        this.Command.MyInvocation,
                                        e.Message);

                                // This exception is caused because failure happens when retrieving the dynamic parameters,
                                // this is not caused by introducing the default parameter binding.
                                throw bindingException;
                            }

                            if (dynamicParamBindableObject != null)
                            {
                                ParameterBinderBase.bindingTracer.WriteLine(
                                    "DYNAMIC parameter object: [{0}]",
                                    dynamicParamBindableObject.GetType());

                                s_tracer.WriteLine("Creating a new parameter binder for the dynamic parameter object");

                                InternalParameterMetadata dynamicParameterMetadata;

                                if (dynamicParamBindableObject is RuntimeDefinedParameterDictionary runtimeParamDictionary)
                                {
                                    // Generate the type metadata for the runtime-defined parameters
                                    dynamicParameterMetadata =
                                        InternalParameterMetadata.Get(runtimeParamDictionary, true, true);

                                    _dynamicParameterBinder =
                                        new RuntimeDefinedParameterBinder(
                                            runtimeParamDictionary,
                                            this.Command,
                                            this.CommandLineParameters);
                                }
                                else
                                {
                                    // Generate the type metadata or retrieve it from the cache
                                    dynamicParameterMetadata =
                                        InternalParameterMetadata.Get(dynamicParamBindableObject.GetType(), Context, true);

                                    // Create the parameter binder for the dynamic parameter object

                                    _dynamicParameterBinder =
                                        new ReflectionParameterBinder(
                                            dynamicParamBindableObject,
                                            this.Command,
                                            this.CommandLineParameters);
                                }

                                // Now merge the metadata with other metadata for the command

                                var dynamicParams =
                                    BindableParameters.AddMetadataForBinder(
                                        dynamicParameterMetadata,
                                        ParameterBinderAssociation.DynamicParameters);
                                foreach (var param in dynamicParams)
                                {
                                    UnboundParameters.Add(param);
                                }

                                // Now set the parameter set flags for the new type metadata.
                                _commandMetadata.DefaultParameterSetFlag =
                                    this.BindableParameters.GenerateParameterSetMappingFromMetadata(_commandMetadata.DefaultParameterSetName);
                            }
                        }

                        if (_dynamicParameterBinder == null)
                        {
                            s_tracer.WriteLine("No dynamic parameter object was returned from the Cmdlet");
                            return;
                        }

                        if (UnboundArguments.Count > 0)
                        {
                            using (ParameterBinderBase.bindingTracer.TraceScope(
                                    "BIND NAMED args to DYNAMIC parameters"))
                            {
                                // Try to bind the unbound arguments as static parameters to the
                                // dynamic parameter object.

                                ReparseUnboundArguments();

                                UnboundArguments = BindNamedParameters(ParameterSetResolver.CurrentParameterSetFlag, UnboundArguments);
                            }

                            using (ParameterBinderBase.bindingTracer.TraceScope(
                                    "BIND POSITIONAL args to DYNAMIC parameters"))
                            {
                                UnboundArguments =
                                    BindPositionalParameters(
                                    UnboundArguments,
                                    ParameterSetResolver.CurrentParameterSetFlag,
                                    _commandMetadata.DefaultParameterSetFlag,
                                    out outgoingBindingException);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This method determines if the unbound mandatory parameters take pipeline input or
        /// if we can use the default parameter set.  If all the unbound mandatory parameters
        /// take pipeline input and the default parameter set is valid, then the default parameter
        /// set is set as the current parameter set and processing can continue.  If there are
        /// more than one valid parameter sets and the unbound mandatory parameters are not
        /// consistent across parameter sets or there is no default parameter set then a
        /// ParameterBindingException is thrown with an errorId of AmbiguousParameterSet.
        /// </summary>
        /// <remarks>
        /// Pipeline mandatory-parameter resolution intentionally preserves viable sets for later pipeline
        /// binding when possible instead of aggressively latching onto a single set.
        ///
        /// Scenario 1: Valid sets A and B share a common pipelineable mandatory parameter; B also has an
        /// additional pipelineable mandatory parameter of a different type. Preserve A so pipeline input
        /// that matches the common parameter type can still bind without forced coercion through B.
        ///
        /// Scenario 2: Same as Scenario 1 but with additional sets C and Default that have no extra
        /// mandatory parameters. Preserve all non-conflicting sets (including Default) to avoid
        /// premature resolution to B.
        ///
        /// Scenario 3: Valid sets A, B, and C each have different mandatory parameter configurations —
        /// A has a pipelineable mandatory, B has a nonpipelineable mandatory, and C has only a
        /// pipelineable non-mandatory. Preserve C when latching onto A would force an incompatible
        /// binding path for the incoming pipeline object type.
        ///
        /// Scenario 4: Same as Scenario 3 but with a Default set that has no mandatory parameters.
        /// Preserve C and Default so subsequent pipeline binding can choose a compatible set.
        /// </remarks>
        /// 
        /// <example>
        /// <para><strong>Scenario 1</strong> — Sets: A (common pipelineable mandatory), B (common + extra pipelineable mandatory).</para>
        /// <code>
        /// function Get-Cmdlet {
        ///     [CmdletBinding()]
        ///     param(
        ///         [Parameter(Mandatory=$true, ValueFromPipeline=$true)]
        ///         [System.DateTime] $Date,
        ///
        ///         [Parameter(ParameterSetName="computer")]
        ///         [Parameter(ParameterSetName="session")]
        ///         $ComputerName,
        ///
        ///         [Parameter(ParameterSetName="session", Mandatory=$true, ValueFromPipeline=$true)]
        ///         [System.TimeSpan] $TimeSpan
        ///     )
        ///     process { Write-Output $PSCmdlet.ParameterSetName }
        /// }
        /// # Get-Date | Get-Cmdlet → "computer"
        /// # If mandatory resolution aggressively latches onto set "session" (B), pipeline binding fails.
        /// # Preserving set "computer" (A) allows pipeline binding to succeed.
        /// </code>
        /// </example>
        /// 
        /// <example>
        /// <para><strong>Scenario 2</strong> — Sets: A, B (same as Scenario 1), plus C and Default with no extra mandatory parameters.</para>
        /// <code>
        /// function Get-Cmdlet {
        ///     [CmdletBinding(DefaultParameterSetName="computer")]
        ///     param(
        ///         [Parameter(ParameterSetName="new")]
        ///         $NewName,
        ///
        ///         [Parameter(Mandatory=$true, ValueFromPipeline=$true)]
        ///         [System.DateTime] $Date,
        ///
        ///         [Parameter(ParameterSetName="computer")]
        ///         [Parameter(ParameterSetName="session")]
        ///         $ComputerName,
        ///
        ///         [Parameter(ParameterSetName="session", Mandatory=$true, ValueFromPipeline=$true)]
        ///         [System.TimeSpan] $TimeSpan
        ///     )
        ///     process { Write-Output $PSCmdlet.ParameterSetName }
        /// }
        /// # Get-Date | Get-Cmdlet → "computer"
        /// # Aggressively resolving to "session" (B) loses viable sets. Preserving A, C, and Default keeps them available.
        /// </code>
        /// </example>
        /// 
        /// <example>
        /// <para><strong>Scenario 3</strong> — Sets: A (pipelineable mandatory), B (nonpipelineable mandatory), C (pipelineable non-mandatory only).</para>
        /// <code>
        /// function Get-Cmdlet {
        ///     [CmdletBinding()]
        ///     param(
        ///         [Parameter(ParameterSetName="network", Mandatory=$true, ValueFromPipeline=$true)]
        ///         [TimeSpan] $network,
        ///
        ///         [Parameter(ParameterSetName="computer", ValueFromPipelineByPropertyName=$true)]
        ///         [string[]] $ComputerName,
        ///
        ///         [Parameter(ParameterSetName="computer", Mandatory=$true)]
        ///         [switch] $DisableComputer,
        ///
        ///         [Parameter(ParameterSetName="session", ValueFromPipeline=$true)]
        ///         [DateTime] $Date
        ///     )
        ///     process { Write-Output $PSCmdlet.ParameterSetName }
        /// }
        /// # Get-Date | Get-Cmdlet → "session"
        /// # If mandatory resolution latches onto "network" (A), pipeline binding fails.
        /// # Preserving set "session" (C) allows pipeline binding to succeed.
        /// </code>
        /// </example>
        /// 
        /// <example>
        /// <para><strong>Scenario 4</strong> — Same as Scenario 3 plus a Default set with no mandatory parameters.</para>
        /// <code>
        /// function Get-Cmdlet {
        ///     [CmdletBinding(DefaultParameterSetName="server")]
        ///     param(
        ///         [Parameter(ParameterSetName="network", Mandatory=$true, ValueFromPipeline=$true)]
        ///         [TimeSpan] $network,
        ///
        ///         [Parameter(ParameterSetName="computer", ValueFromPipelineByPropertyName=$true)]
        ///         [string[]] $ComputerName,
        ///
        ///         [Parameter(ParameterSetName="computer", Mandatory=$true)]
        ///         [switch] $DisableComputer,
        ///
        ///         [Parameter(ParameterSetName="session")]
        ///         [Parameter(ParameterSetName="server")]
        ///         [string] $Param,
        ///
        ///         [Parameter(ValueFromPipeline=$true)]
        ///         [DateTime] $Date
        ///     )
        ///     process { Write-Output $PSCmdlet.ParameterSetName }
        /// }
        /// # Get-Date | Get-Cmdlet → "server"
        /// # Aggressively resolving to "network" (A) loses viable sets. Preserving C and Default keeps them available.
        /// </code>
        /// </example>
        /// <param name="validParameterSetCount">
        /// The number of valid parameter sets.
        /// </param>
        /// <param name="isPipelineInputExpected">
        /// True if the pipeline is open to receive input.
        /// </param>
        /// <exception cref="ParameterBindingException">
        /// If there are multiple valid parameter sets and the missing mandatory parameters are
        /// not consistent across parameter sets, or there is no default parameter set.
        /// </exception>
        private Collection<MergedCompiledCommandParameter> GetMissingMandatoryParameters(
            int validParameterSetCount,
            bool isPipelineInputExpected)
        {
            Collection<MergedCompiledCommandParameter> result = new Collection<MergedCompiledCommandParameter>();

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
                    commandMandatorySets = ParameterSetResolver.CurrentParameterSetFlag;
                }

                if (missingAMandatoryParameterInAllSet)
                {
                    uint availableParameterSetFlags = this.BindableParameters.AllParameterSetFlags;
                    if (availableParameterSetFlags == 0)
                    {
                        availableParameterSetFlags = uint.MaxValue;
                    }

                    commandMandatorySets = (ParameterSetResolver.CurrentParameterSetFlag & availableParameterSetFlags);
                }

                // First we need to see if there are multiple valid parameter sets, and if one is
                // the default parameter set, and it is not missing any mandatory parameters, then
                // use the default parameter set.

                if (validParameterSetCount > 1 &&
                    defaultParameterSet != 0 &&
                    (defaultParameterSet & commandMandatorySets) == 0 &&
                    (defaultParameterSet & ParameterSetResolver.CurrentParameterSetFlag) != 0)
                {
                    // If no other set takes pipeline input, then latch on to the default set

                    uint setThatTakesPipelineInput = 0;
                    foreach (ParameterSetPromptingData promptingSetData in promptingData.Values)
                    {
                        if ((promptingSetData.ParameterSet & ParameterSetResolver.CurrentParameterSetFlag) != 0 &&
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
                        // Old algorithm starts
                        // // latch on to the default parameter set
                        // commandMandatorySets = defaultParameterSet;
                        // ParameterSetResolver.CurrentParameterSetFlag = defaultParameterSet;
                        // Command.SetParameterSetName(CurrentParameterSetName);
                        // Old algorithm ends

                        // At this point, we have the following information:
                        //  1. There are unbound mandatory parameter(s)
                        //  2. No unbound mandatory parameter is in AllSet
                        //  3. All unbound mandatory parameters don't take pipeline input
                        //  4. Default parameter set is valid
                        //  5. Default parameter set doesn't contain unbound mandatory parameters
                        //
                        // We ignore those parameter sets that contain unbound mandatory parameters, but leave
                        // all other parameter sets remain valid. The other parameter sets contains the default
                        // parameter set and have one characteristic: NONE of them contain unbound mandatory parameters
                        //
                        // Comparing to the old algorithm, we keep more possible parameter sets here, but
                        // we need to prioritize the default parameter set for pipeline binding, so as NOT to
                        // make breaking changes. This is to handle the following scenario:
                        //                               Old Algorithm              New Algorithm (without prioritizing default)      New Algorithm (with prioritizing default)
                        //  Remaining Parameter Sets       A(default)               A(default), B                                     A(default), B
                        //        Pipeline parameter       P1(string)               A: P1(string); B: P2(System.DateTime)             A: P1(string); B: P2(System.DateTime)
                        //   Pipeline parameter type       P1:By Value              P1:By Value; P2:By Value                          P1:By Value; P2:By Value
                        //            Pipeline input       $a (System.DateTime)     $a (System.DateTime)                              $a (System.DateTime)
                        //   Pipeline binding result       P1 --> $a.ToString()     P2 --> $a                                         P1 --> $a.ToString()
                        //     Pipeline binding type       ByValueWithCoercion      ByValueWithoutCoercion                            ByValueWithCoercion

                        commandMandatorySets = ParameterSetResolver.CurrentParameterSetFlag & (~commandMandatorySets);
                        ParameterSetResolver.CurrentParameterSetFlag = commandMandatorySets;

                        if (ParameterSetResolver.CurrentParameterSetFlag == defaultParameterSet)
                            Command.SetParameterSetName(ParameterSetResolver.CurrentParameterSetName);
                        else
                            // Prioritize the default set during pipeline binding to preserve previous behavior
                            // while still keeping additional viable sets available.
                            ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding = defaultParameterSet;
                    }
                }
                // We need to analyze the prompting data that was gathered to determine what parameter
                // set to use, which parameters need prompting for, and which parameters take pipeline input.

                int commandMandatorySetsCount = ParameterSetResolver.ValidParameterSetCount(commandMandatorySets);
                if (commandMandatorySetsCount == 0)
                {
                    ParameterSetResolver.ThrowAmbiguousParameterSetException(ParameterSetResolver.CurrentParameterSetFlag);
                }
                else if (commandMandatorySetsCount == 1)
                {
                    // Since we have only one valid parameter set, add all.
                    CollectNonpipelineableMandatoryParameters(promptingData, commandMandatorySets, result);
                }
                else if (ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding == 0)
                {
                    if (!TryLatchToDefaultParameterSet(promptingData, commandMandatorySets, defaultParameterSet, result))
                    {
                        TryLatchToUniquePipelineSet(promptingData, commandMandatorySets, result);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Builds prompting data for unbound mandatory parameters.
        /// </summary>
        private Dictionary<uint, ParameterSetPromptingData> CollectMandatoryPromptingData(
            uint defaultParameterSet,
            bool isPipelineInputExpected,
            Collection<MergedCompiledCommandParameter> result,
            ref uint commandMandatorySets,
            out bool missingAMandatoryParameter,
            out bool missingAMandatoryParameterInAllSet)
        {
            Dictionary<uint, ParameterSetPromptingData> promptingData = new Dictionary<uint, ParameterSetPromptingData>();

            missingAMandatoryParameter = false;
            missingAMandatoryParameterInAllSet = false;

            // See if any of the unbound parameters are mandatory.
            foreach (MergedCompiledCommandParameter parameter in UnboundParameters)
            {
                // If a parameter is never mandatory, we can skip lots of work here.
                if (!parameter.Parameter.IsMandatoryInSomeParameterSet)
                {
                    continue;
                }

                var matchingParameterSetMetadata = parameter.Parameter.GetMatchingParameterSetData(ParameterSetResolver.CurrentParameterSetFlag);

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
                        parameterMandatorySets |= (ParameterSetResolver.CurrentParameterSetFlag & newMandatoryParameterSetFlag);
                        commandMandatorySets |= (ParameterSetResolver.CurrentParameterSetFlag & parameterMandatorySets);
                    }
                    else
                    {
                        missingAMandatoryParameterInAllSet = true;
                    }
                }

                // We are not expecting pipeline input.
                if (!isPipelineInputExpected && thisParameterMissing)
                {
                    result.Add(parameter);
                }
            }

            return promptingData;
        }

        /// <summary>
        /// Collects non-pipelineable mandatory parameters for the mandatory sets.
        /// </summary>
        private static void CollectNonpipelineableMandatoryParameters(
            Dictionary<uint, ParameterSetPromptingData> promptingData,
            uint mandatorySets,
            Collection<MergedCompiledCommandParameter> result)
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

        /// <summary>
        /// Attempts to latch parameter set resolution to the default parameter set.
        /// </summary>
        private bool TryLatchToDefaultParameterSet(
            Dictionary<uint, ParameterSetPromptingData> promptingData,
            uint commandMandatorySets,
            uint defaultParameterSet,
            Collection<MergedCompiledCommandParameter> result)
        {
            if (defaultParameterSet == 0 || (commandMandatorySets & defaultParameterSet) == 0)
            {
                return false;
            }

            // Determine if another set could be satisfied by pipeline input - that is, it
            // has mandatory pipeline input parameters but no mandatory command-line only parameters.
            bool anotherSetTakesPipelineInput = false;
            foreach (ParameterSetPromptingData paramPromptingData in promptingData.Values)
            {
                if (!paramPromptingData.IsAllSet &&
                    !paramPromptingData.IsDefaultSet &&
                    paramPromptingData.PipelineableMandatoryParameters.Count > 0 &&
                    paramPromptingData.NonpipelineableMandatoryParameters.Count == 0)
                {
                    anotherSetTakesPipelineInput = true;
                    break;
                }
            }

            // Determine if another set takes pipeline input by property name.
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

            // See if we should pick the default set if it can bind strongly to the incoming objects.
            bool latchOnToDefault = false;
            if (promptingData.TryGetValue(defaultParameterSet, out ParameterSetPromptingData defaultSetPromptingData))
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

            // If only the all set takes pipeline input then latch on to the default set.
            if (!latchOnToDefault && !anotherSetTakesPipelineInput)
            {
                latchOnToDefault = true;
            }

            if (!latchOnToDefault && promptingData.TryGetValue(uint.MaxValue, out ParameterSetPromptingData allSetPromptingData))
            {
                latchOnToDefault = allSetPromptingData.NonpipelineableMandatoryParameters.Count > 0;
            }

            if (!latchOnToDefault)
            {
                return false;
            }

            // Latch on to the default parameter set.
            ParameterSetResolver.CurrentParameterSetFlag = defaultParameterSet;
            Command.SetParameterSetName(ParameterSetResolver.CurrentParameterSetName);

            CollectNonpipelineableMandatoryParameters(promptingData, defaultParameterSet, result);
            return true;
        }

        /// <summary>
        /// Attempts to latch parameter set resolution to a unique pipeline-input parameter set.
        /// </summary>
        private bool TryLatchToUniquePipelineSet(
            Dictionary<uint, ParameterSetPromptingData> promptingData,
            uint commandMandatorySets,
            Collection<MergedCompiledCommandParameter> result)
        {
            // Scenario 1-4: preserve viable parameter sets for subsequent pipeline binding;
            // see this method's <remarks> for detailed scenario descriptions.

            uint setThatTakesPipelineInputByValue = 0;
            uint setThatTakesPipelineInputByPropertyName = 0;

            // Find the single set that takes pipeline input by value.
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

            // Find the single set that takes pipeline input by property name.
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

            // If we have one or the other, we can latch onto that set without difficulty.
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

                // Add all missing mandatory parameters that don't take pipeline input.
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

                // Preserve potential parameter sets as much as possible.
                PreservePotentialParameterSets(uniqueSetThatTakesPipelineInput,
                                               otherMandatorySetsToBeIgnored,
                                               chosenMandatorySetContainsNonpipelineableMandatoryParameters);

                return true;
            }

            // Now if any valid parameter sets have nonpipelineable mandatory parameters we have
            // an error.
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

            // As a last-ditch effort, bind to the set that takes pipeline input by value.
            if (setThatTakesPipelineInputByValue != 0)
            {
                uint otherMandatorySetsToBeIgnored = 0;
                bool chosenMandatorySetContainsNonpipelineableMandatoryParameters = false;

                // Add all missing mandatory parameters that don't take pipeline input.
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

                // Preserve potential parameter sets as much as possible.
                PreservePotentialParameterSets(setThatTakesPipelineInputByValue,
                                               otherMandatorySetsToBeIgnored,
                                               chosenMandatorySetContainsNonpipelineableMandatoryParameters);
                return false;
            }

            if ((!foundMultipleSetsThatTakesPipelineInputByValue) &&
               (!foundMultipleSetsThatTakesPipelineInputByPropertyName))
            {
                ParameterSetResolver.ThrowAmbiguousParameterSetException(ParameterSetResolver.CurrentParameterSetFlag);
            }

            // Remove the data set that contains non-pipelineable mandatory parameters, since we are not
            // prompting for them and they will not be bound later.
            // If no data set left, throw ambiguous parameter set exception.
            if (setsThatContainNonpipelineableMandatoryParameter != 0)
            {
                IgnoreOtherMandatoryParameterSets(setsThatContainNonpipelineableMandatoryParameter);
                if (ParameterSetResolver.CurrentParameterSetFlag == 0)
                {
                    ParameterSetResolver.ThrowAmbiguousParameterSetException(ParameterSetResolver.CurrentParameterSetFlag);
                }

                if (ParameterSetResolver.ValidParameterSetCount(ParameterSetResolver.CurrentParameterSetFlag) == 1)
                {
                    Command.SetParameterSetName(ParameterSetResolver.CurrentParameterSetName);
                }
            }

            return false;
        }

        /// <summary>
        /// Preserve potential parameter sets as much as possible.
        /// </summary>
        /// <param name="chosenMandatorySet">The mandatory set we choose to latch on.</param>
        /// <param name="otherMandatorySetsToBeIgnored">Other mandatory parameter sets to be ignored.</param>
        /// <param name="chosenSetContainsNonpipelineableMandatoryParameters">Indicate if the chosen mandatory set contains any non-pipelineable mandatory parameters.</param>
        private void PreservePotentialParameterSets(uint chosenMandatorySet, uint otherMandatorySetsToBeIgnored, bool chosenSetContainsNonpipelineableMandatoryParameters)
        {
            // If the chosen set contains nonpipelineable mandatory parameters, then we set it as the only valid parameter set since we will prompt for those mandatory parameters
            if (chosenSetContainsNonpipelineableMandatoryParameters)
            {
                ParameterSetResolver.CurrentParameterSetFlag = chosenMandatorySet;
                Command.SetParameterSetName(ParameterSetResolver.CurrentParameterSetName);
            }
            else
            {
                // Otherwise, we additionally preserve those valid parameter sets that contain no mandatory parameter, or contain only the common mandatory parameters
                IgnoreOtherMandatoryParameterSets(otherMandatorySetsToBeIgnored);
                Command.SetParameterSetName(ParameterSetResolver.CurrentParameterSetName);

                if (ParameterSetResolver.CurrentParameterSetFlag != chosenMandatorySet)
                {
                    ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding = chosenMandatorySet;
                }
            }
        }

        /// <summary>
        /// Update ParameterSetResolver.CurrentParameterSetFlag to ignore the specified mandatory sets.
        /// </summary>
        /// <remarks>
        /// This method is used only when we try to preserve parameter sets during the mandatory parameter checking.
        /// In cases where this method is used, there must be at least one parameter set declared.
        /// </remarks>
        /// <param name="otherMandatorySetsToBeIgnored">The mandatory parameter sets to be ignored.</param>
        private void IgnoreOtherMandatoryParameterSets(uint otherMandatorySetsToBeIgnored)
        {
            if (otherMandatorySetsToBeIgnored == 0)
                return;

            if (ParameterSetResolver.CurrentParameterSetFlag == uint.MaxValue)
            {
                // We cannot update the ParameterSetResolver.CurrentParameterSetFlag to remove some parameter sets directly when it's AllSet as that will get it to an incorrect state.
                uint availableParameterSets = this.BindableParameters.AllParameterSetFlags;
                Diagnostics.Assert(availableParameterSets != 0, "At least one parameter set must be declared");
                ParameterSetResolver.CurrentParameterSetFlag = availableParameterSets & (~otherMandatorySetsToBeIgnored);
            }
            else
            {
                ParameterSetResolver.NarrowByParameterSetFlags(~otherMandatorySetsToBeIgnored);
            }
        }

        private static uint NewParameterSetPromptingData(
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
                ParameterSetPromptingData promptingDataForSet;
                if (!promptingData.TryGetValue(parameterSetFlag, out promptingDataForSet))
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
            => _mandatoryParameterPrompter.HandleUnboundMandatoryParameters(out missingMandatoryParameters);

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
                    _commandMetadata.Name))
                {
                    // First run any of the delay bind ScriptBlocks and bind the
                    // result to the appropriate parameter.

                    bool thereWasSomethingToBind;
                    bool invokeScriptResult = InvokeAndBindDelayBindScriptBlock(inputToOperateOn, out thereWasSomethingToBind);

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
                this.RestoreDefaultParameterValues(ParametersBoundThroughPipelineInput);

                // Let the parameter binding errors propagate out
                throw;
            }

            try
            {
                // Now make sure we have latched on to a single parameter set.
                ParameterSetResolver.VerifyParameterSetSelected();
            }
            catch (ParameterBindingException)
            {
                // Reset the default values
                // This prevents the last pipeline object from being bound during EndProcessing
                // if it failed some post binding verification step.
                this.RestoreDefaultParameterValues(ParametersBoundThroughPipelineInput);

                throw;
            }

            if (!result)
            {
                // Reset the default values
                // This prevents the last pipeline object from being bound during EndProcessing
                // if it failed some post binding verification step.
                this.RestoreDefaultParameterValues(ParametersBoundThroughPipelineInput);
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

            this.RestoreDefaultParameterValues(ParametersBoundThroughPipelineInput);

            // Now clear the parameter names from the previous pipeline input

            ParametersBoundThroughPipelineInput.Clear();

            // Now restore the parameter set flags

            ParameterSetResolver.CurrentParameterSetFlag = ParameterSetResolver.PrePipelineProcessingParameterSetFlags;
            uint validParameterSets = ParameterSetResolver.CurrentParameterSetFlag;
            bool needToPrioritizeOneSpecificParameterSet = ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding != 0;
            int steps = needToPrioritizeOneSpecificParameterSet ? 2 : 1;

            if (needToPrioritizeOneSpecificParameterSet)
            {
                // ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding is set, so we are certain that the specified parameter set must be valid,
                // and it's not the only valid parameter set.
                Diagnostics.Assert((ParameterSetResolver.CurrentParameterSetFlag & ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding) != 0, "ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding should be valid if it's set");
                validParameterSets = ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding;
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
                            ParameterSetResolver.ValidateParameterSets(true, true, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
                            validParameterSets = ParameterSetResolver.CurrentParameterSetFlag;
                        }

                        result = true;
                    }
                }

                // Update the validParameterSets after the binding attempt for the prioritized parameter set
                if (needToPrioritizeOneSpecificParameterSet && i == 0)
                {
                    // If the prioritized set can be bound successfully, there is no need to do the second round binding
                    if (ParameterSetResolver.CurrentParameterSetFlag == ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding)
                    {
                        break;
                    }

                    validParameterSets = ParameterSetResolver.CurrentParameterSetFlag & (~ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding);
                }
            }

            // Now make sure we only have one valid parameter set
            // Note, this will throw if we have more than one.

            ParameterSetResolver.ValidateParameterSets(false, true, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);

            if (!DefaultParameterBindingInUse)
            {
                if (_defaultParameterValueBinder.ApplyDefaultParameterBinding(
                    "PIPELINE BIND",
                    isDynamic: false,
                    currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
                {
                    DefaultParameterBindingInUse = true;
                }
            }

            return result;
        }

        private bool BindUnboundParametersForBindingState(
            PSObject inputToOperateOn,
            CurrentlyBinding currentlyBinding,
            uint validParameterSets)
        {
            bool aParameterWasBound = false;

            // First check to see if the default parameter set has been defined and if it
            // is still valid.

            uint defaultParameterSetFlag = _commandMetadata.DefaultParameterSetFlag;

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

            for (int i = UnboundParameters.Count - 1; i >= 0; i--)
            {
                var parameter = UnboundParameters[i];

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

            ParameterBinderBase.bindingTracer.WriteLine(
                ((flags & ParameterBindingFlags.ShouldCoerceType) != 0) ?
                    "Parameter [{0}] PIPELINE INPUT ValueFromPipelineByPropertyName WITH COERCION" :
                    "Parameter [{0}] PIPELINE INPUT ValueFromPipelineByPropertyName NO COERCION",
                parameter.Parameter.Name);

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
                ThrowOrElaborateBindingException(parameterBindingException);
            }

            return bindResult;
        }

        [DoesNotReturn]
        private void ThrowOrElaborateBindingException(ParameterBindingException ex)
        {
            if (!DefaultParameterBindingInUse)
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            ThrowElaboratedBindingException(ex);
        }

        /// <summary>
        /// Used for defining the state of the binding state machine.
        /// </summary>
        private enum CurrentlyBinding
        {
            ValueFromPipelineNoCoercion = 0,
            ValueFromPipelineByPropertyNameNoCoercion = 1,
            ValueFromPipelineWithCoercion = 2,
            ValueFromPipelineByPropertyNameWithCoercion = 3
        }

        /// <summary>
        /// Invokes any delay bind script blocks and binds the resulting value
        /// to the appropriate parameter.
        /// </summary>
        /// <param name="inputToOperateOn">
        /// The input to the script block.
        /// </param>
        /// <param name="thereWasSomethingToBind">
        /// Returns True if there was a ScriptBlock to invoke and bind, or false if there
        /// are no ScriptBlocks to invoke.
        /// </param>
        /// <returns>
        /// True if the binding succeeds, or false otherwise.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// if <paramref name="inputToOperateOn"/> is null.
        /// </exception>
        /// <exception cref="ParameterBindingException">
        /// If execution of the script block throws an exception or if it doesn't produce
        /// any output.
        /// </exception>
        private bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind)
        {
            thereWasSomethingToBind = false;
            bool result = true;

            // NOTE: we are not doing backup and restore of default parameter
            // values here.  It is not needed because each script block will be
            // invoked and each delay bind parameter bound for each pipeline object.
            // This is unlike normal pipeline object processing which may bind
            // different parameters depending on the type of the incoming pipeline
            // object.

            // Loop through each of the delay bind script blocks and invoke them.
            // Bind the result to the associated parameter

            foreach (KeyValuePair<MergedCompiledCommandParameter, DelayedScriptBlockArgument> delayedScriptBlock in _delayBindScriptBlocks)
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
                    if (delayedScriptBlock.Value._parameterBinder == this)
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
                        this.Command.MyInvocation,
                        GetErrorExtent(argument),
                        parameter.Parameter.Name,
                        null,
                        null,
                        error.Message);
                }

                if (output == null || output.Count == 0)
                {
                    ParameterBindingException.ThrowScriptBlockArgumentNoOutput(
                        this.Command.MyInvocation,
                        GetErrorExtent(argument),
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

                if (!BindToAssociatedBinder(newArgument, parameter, ParameterBindingFlags.ShouldCoerceType))
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the binder instance responsible for the given parameter.
        /// </summary>
        /// <param name="parameter">
        /// The parameter metadata.
        /// </param>
        /// <returns>
        /// The binder that should bind this parameter, or null if no binder applies.
        /// </returns>
        private ParameterBinderBase GetBinderForParameter(MergedCompiledCommandParameter parameter)
        {
            return parameter.BinderAssociation switch
            {
                ParameterBinderAssociation.DeclaredFormalParameters => DefaultParameterBinder,
                ParameterBinderAssociation.CommonParameters => _commonParametersBinder,
                ParameterBinderAssociation.ShouldProcessParameters => _shouldProcessParameterBinder,
                ParameterBinderAssociation.PagingParameters => _pagingParameterBinder,
                ParameterBinderAssociation.TransactionParameters => _transactionParameterBinder,
                ParameterBinderAssociation.DynamicParameters => _dynamicParameterBinder,
                _ => null,
            };
        }

        #endregion helper_methods

        #region private_members

        /// <summary>
        /// This method gets a backup of the default value of a parameter.
        /// Derived classes may override this method to get the default parameter
        /// value in a different way.
        /// </summary>
        /// <param name="name">
        /// The name of the parameter to get the default value of.
        /// </param>
        /// <returns>
        /// The value of the parameter specified by name.
        /// </returns>
        /// <exception cref="ParameterBindingParameterDefaultValueException">
        /// If the parameter binder encounters an error getting the default value.
        /// </exception>
        internal object GetDefaultParameterValue(string name)
        {
            MergedCompiledCommandParameter matchingParameter =
                BindableParameters.GetMatchingParameter(
                    name,
                    false,
                    true,
                    null);

            object result = null;

            try
            {
                result = GetBinderForParameter(matchingParameter)?.GetDefaultParameterValue(name);
            }
            catch (GetValueException getValueException)
            {
                ParameterBindingParameterDefaultValueException.ThrowGetDefaultValueFailed(
                    getValueException,
                    this.Command.MyInvocation,
                    name,
                    getValueException.Message);
            }

            return result;
        }

        /// <summary>
        /// Gets or sets the command that this parameter binder controller
        /// will bind parameters to.
        /// </summary>
        internal Cmdlet Command { get; }

        private readonly DefaultParameterValueBinder _defaultParameterValueBinder;
        private readonly MandatoryParameterPrompter _mandatoryParameterPrompter;

        /// <summary>
        /// The cmdlet metadata.
        /// </summary>
        private readonly CommandMetadata _commandMetadata;

        /// <summary>
        /// THe command runtime object for this cmdlet.
        /// </summary>
        private readonly MshCommandRuntime _commandRuntime;

        /// <summary>
        /// Keep the obsolete parameter warnings generated from parameter binding.
        /// </summary>
        internal List<WarningRecord> ObsoleteParameterWarningList { get; private set; }

        /// <summary>
        /// Keep names of the parameters for which we have generated obsolete warning messages.
        /// </summary>
        private HashSet<string> BoundObsoleteParameterNames
        {
            get
            {
                return _boundObsoleteParameterNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private HashSet<string> _boundObsoleteParameterNames;

        /// <summary>
        /// The parameter binder for the dynamic parameters. Currently this
        /// can be either a ReflectionParameterBinder or a RuntimeDefinedParameterBinder.
        /// </summary>
        private ParameterBinderBase _dynamicParameterBinder;

        private readonly ReflectionParameterBinder _commonParametersBinder;
        private readonly ReflectionParameterBinder _shouldProcessParameterBinder;
        private readonly ReflectionParameterBinder _pagingParameterBinder;
        private readonly ReflectionParameterBinder _transactionParameterBinder;

        private sealed class DelayedScriptBlockArgument
        {
            // Remember the parameter binder so we know when to invoke the script block
            // and when to use the evaluated argument.
            internal CmdletParameterBinderController _parameterBinder;
            internal CommandParameterInternal _argument;
            internal Collection<PSObject> _evaluatedArgument;

            public override string ToString()
            {
                return _argument.ArgumentValue.ToString();
            }
        }

        /// <summary>
        /// This dictionary is used to contain the arguments that were passed in as ScriptBlocks
        /// but the parameter isn't a ScriptBlock. So we have to wait to bind the parameter
        /// until there is a pipeline object available to invoke the ScriptBlock with.
        /// </summary>
        private readonly Dictionary<MergedCompiledCommandParameter, DelayedScriptBlockArgument> _delayBindScriptBlocks =
            new Dictionary<MergedCompiledCommandParameter, DelayedScriptBlockArgument>();

        /// <summary>
        /// A collection of the default values of the parameters.
        /// </summary>
        private readonly Dictionary<string, CommandParameterInternal> _defaultParameterValues =
            new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        #endregion private_members

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
                BackupDefaultParameter(parameter);

                // Now bind the new value
                CommandParameterInternal param = CommandParameterInternal.CreateParameterWithArgument(
                    /*parameterAst*/null, parameter.Parameter.Name, "-" + parameter.Parameter.Name + ":",
                    /*argumentAst*/null, parameterValue,
                    false);

                flags &= ~ParameterBindingFlags.DelayBindScriptBlock;
                result = DispatchBindToSubBinder(ParameterSetResolver.CurrentParameterSetFlag, param, parameter, flags);

                if (result)
                {
                    // Now make sure to remember that the default value needs to be restored
                    // if we get another pipeline object
                    ParametersBoundThroughPipelineInput.Add(parameter);
                }
            }

            return result;
        }

        protected override void SaveDefaultScriptParameterValue(string name, object value)
        {
            _defaultParameterValues.Add(name,
                CommandParameterInternal.CreateParameterWithArgument(
                    /*parameterAst*/null, name, "-" + name + ":",
                    /*argumentAst*/null, value,
                    false));
        }

        /// <summary>
        /// Backs up the specified parameter value by calling the GetDefaultParameterValue
        /// abstract method.
        ///
        /// This method is called when binding a parameter value that came from a pipeline
        /// object.
        /// </summary>
        /// <exception cref="ParameterBindingParameterDefaultValueException">
        /// If the parameter binder encounters an error getting the default value.
        /// </exception>
        private void BackupDefaultParameter(MergedCompiledCommandParameter parameter)
        {
            if (!_defaultParameterValues.ContainsKey(parameter.Parameter.Name))
            {
                object defaultParameterValue = GetDefaultParameterValue(parameter.Parameter.Name);
                _defaultParameterValues.Add(
                    parameter.Parameter.Name,
                    CommandParameterInternal.CreateParameterWithArgument(
                        /*parameterAst*/null, parameter.Parameter.Name, "-" + parameter.Parameter.Name + ":",
                        /*argumentAst*/null, defaultParameterValue,
                        false));
            }
        }

        /// <summary>
        /// Replaces the values of the parameters with their initial value for the
        /// parameters specified.
        /// </summary>
        /// <param name="parameters">
        /// The parameters that should have their default values restored.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="parameters"/> is null.
        /// </exception>
        private void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters)
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

                CommandParameterInternal argumentToBind = null;

                // If the argument was found then bind it to the parameter
                // and manage the bound and unbound parameter list

                if (_defaultParameterValues.TryGetValue(parameter.Parameter.Name, out argumentToBind))
                {
                    // Don't go through the normal binding routine to run data generation,
                    // type coercion, validation, or prerequisites since we know the
                    // type is already correct, and we don't want data generation to
                    // run when resetting the default value.

                    Exception error = null;
                    try
                    {
                        // We shouldn't have to coerce the type here so its
                        // faster to pass false

                        bool bindResult = RestoreParameter(argumentToBind, parameter);

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
                        Type specifiedType = argumentToBind.ArgumentValue?.GetType();
                        ParameterBindingException.ThrowParameterBindingFailed(
                            error,
                            this.InvocationInfo,
                            GetErrorExtent(argumentToBind),
                            parameter.Parameter.Name,
                            parameter.Parameter.Type,
                            specifiedType,
                            error.Message);
                    }

                    // Since the parameter was returned to its original value,
                    // ensure that it is not in the boundParameters list but
                    // is in the unboundParameters list

                    BoundParameters.Remove(parameter.Parameter.Name);

                    if (!UnboundParameters.Contains(parameter))
                    {
                        UnboundParameters.Add(parameter);
                    }

                    BoundArguments.Remove(parameter.Parameter.Name);
                }
                else
                {
                    // Since the parameter was not reset, ensure that the parameter
                    // is in the bound parameters list and not in the unbound
                    // parameters list

                    if (!BoundParameters.ContainsKey(parameter.Parameter.Name))
                    {
                        BoundParameters.Add(parameter.Parameter.Name, parameter);
                    }

                    // Ensure the parameter is not in the unboundParameters list

                    UnboundParameters.Remove(parameter);
                }
            }
        }
    }

}
