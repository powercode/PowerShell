// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

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
    internal class CmdletParameterBinderController : ParameterBinderController, IParameterBindingContext, IDefaultParameterBindingContext, IMandatoryParameterPrompterContext, IPipelineParameterBindingContext, IDelayBindScriptBlockContext, IDynamicParameterHandlerContext, IDefaultValueManagerContext
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
            if (cmdlet! == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(cmdlet));
            }

            if (commandMetadata! == null)
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

            // Record the command name in the binding state for debugger display.
            State.CommandName = commandMetadata.Name;

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

            _pipelineParameterBinder = new PipelineParameterBinder(this);
            _delayBindScriptBlockHandler = new DelayBindScriptBlockHandler(this);
            _dynamicParameterHandler = new DynamicParameterHandler(this);
            _defaultValueManager = new DefaultValueManager(this);
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

        IList<MergedCompiledCommandParameter> IPipelineParameterBindingContext.UnboundParameters => UnboundParameters;

        Collection<MergedCompiledCommandParameter> IPipelineParameterBindingContext.ParametersBoundThroughPipelineInput
            => ParametersBoundThroughPipelineInput;

        ParameterSetResolver IPipelineParameterBindingContext.ParameterSetResolver => ParameterSetResolver;

        bool IPipelineParameterBindingContext.DefaultParameterBindingInUse
        {
            get => DefaultParameterBindingInUse;
            set => DefaultParameterBindingInUse = value;
        }

        uint IPipelineParameterBindingContext.DefaultParameterSetFlag => _commandMetadata.DefaultParameterSetFlag;

        string IPipelineParameterBindingContext.CommandName => _commandMetadata.Name;

        bool IPipelineParameterBindingContext.InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind)
            => _delayBindScriptBlockHandler.InvokeAndBind(inputToOperateOn, out thereWasSomethingToBind);

        InvocationInfo IDelayBindScriptBlockContext.InvocationInfo => Command.MyInvocation;

        IScriptExtent IDelayBindScriptBlockContext.GetErrorExtent(CommandParameterInternal argument)
            => GetErrorExtent(argument);

        bool IDelayBindScriptBlockContext.BindToAssociatedBinder(
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
            => BindToAssociatedBinder(argument, parameter, flags);

        void IPipelineParameterBindingContext.BackupDefaultParameter(MergedCompiledCommandParameter parameter)
            => _defaultValueManager.Backup(parameter);

        void IPipelineParameterBindingContext.RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters)
            => _defaultValueManager.Restore(parameters);

        bool IPipelineParameterBindingContext.DispatchBindToSubBinder(
            uint validParameterSetFlag,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
            => DispatchBindToSubBinder(validParameterSetFlag, argument, parameter, flags);

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        void IPipelineParameterBindingContext.ThrowOrElaborateBindingException(ParameterBindingException ex)
            => ThrowOrElaborateBindingException(ex);

        bool IPipelineParameterBindingContext.ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag)
            => _defaultParameterValueBinder.ApplyDefaultParameterBinding(caller, isDynamic, currentParameterSetFlag);

        bool IDynamicParameterHandlerContext.ImplementsDynamicParameters
            => _commandMetadata.ImplementsDynamicParameters;

        Cmdlet IDynamicParameterHandlerContext.Command => Command;

        ExecutionContext IDynamicParameterHandlerContext.Context => Context;

        InvocationInfo IDynamicParameterHandlerContext.InvocationInfo => Command.MyInvocation;

        MergedCommandParameterMetadata IDynamicParameterHandlerContext.BindableParameters => BindableParameters;

        IList<MergedCompiledCommandParameter> IDynamicParameterHandlerContext.UnboundParameters => UnboundParameters;

        Collection<CommandParameterInternal> IDynamicParameterHandlerContext.UnboundArguments
        {
            get => UnboundArguments;
            set => UnboundArguments = value;
        }

        uint IDynamicParameterHandlerContext.CurrentParameterSetFlag => ParameterSetResolver.CurrentParameterSetFlag;

        uint IDynamicParameterHandlerContext.DefaultParameterSetFlag
        {
            get => _commandMetadata.DefaultParameterSetFlag;
            set => _commandMetadata.DefaultParameterSetFlag = value;
        }

        string IDynamicParameterHandlerContext.DefaultParameterSetName => _commandMetadata.DefaultParameterSetName;

        CommandLineParameters IDynamicParameterHandlerContext.CommandLineParameters => CommandLineParameters;

        void IDynamicParameterHandlerContext.ReparseUnboundArguments() => ReparseUnboundArguments();

        Collection<CommandParameterInternal> IDynamicParameterHandlerContext.BindNamedParameters(
            uint parameterSetFlag, Collection<CommandParameterInternal> args)
            => BindNamedParameters(parameterSetFlag, args);

        Collection<CommandParameterInternal> IDynamicParameterHandlerContext.BindPositionalParameters(
            Collection<CommandParameterInternal> args,
            uint currentParameterSetFlag,
            uint defaultParameterSetFlag,
            out ParameterBindingException? outgoingBindingException)
            => BindPositionalParameters(args, currentParameterSetFlag, defaultParameterSetFlag, out outgoingBindingException);

        InvocationInfo IDefaultValueManagerContext.InvocationInfo => Command.MyInvocation;

        IScriptExtent IDefaultValueManagerContext.GetErrorExtent(CommandParameterInternal argument)
            => GetErrorExtent(argument);

        object? IDefaultValueManagerContext.GetDefaultParameterValue(string name)
            => GetDefaultParameterValue(name);

        bool IDefaultValueManagerContext.RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter)
            => RestoreParameter(argument, parameter);

        Dictionary<string, MergedCompiledCommandParameter> IDefaultValueManagerContext.BoundParameters => BoundParameters;

        IList<MergedCompiledCommandParameter> IDefaultValueManagerContext.UnboundParameters => UnboundParameters;

        Dictionary<string, CommandParameterInternal> IDefaultValueManagerContext.BoundArguments => BoundArguments;

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
                uint filteredValidParameterSetFlags = ParameterSetResolver.FilterParameterSetsTakingNoPipelineInput(_delayBindScriptBlockHandler.Keys);
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

            ParameterBindingException? reportedBindingException;
            ParameterBindingException? currentBindingException;

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
            _dynamicParameterHandler.Handle(out currentBindingException);

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

        internal IDictionary? DefaultParameterValues
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
        private void VerifyArgumentsProcessed(ParameterBindingException? originalBindingException)
        {
            // Now verify that all the arguments that were passed in were processed.

            if (UnboundArguments.Count > 0)
            {
                ParameterBindingException bindingException;
                CommandParameterInternal parameter = UnboundArguments[0];

                // Get the argument type that was specified

                Type? specifiedType = null;
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
                                argument = parameter.ArgumentValue.ToString() ?? StringLiterals.DollarNull;
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
                if ((argumentValue is ScriptBlock || argumentValue is DelayBindScriptBlockHandler.DelayedScriptBlockArgument) &&
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

                    DelayBindScriptBlockHandler.DelayedScriptBlockArgument delayedArg =
                        argumentValue as DelayBindScriptBlockHandler.DelayedScriptBlockArgument
                        ?? _delayBindScriptBlockHandler.CreateEntry(argument);
                    _delayBindScriptBlockHandler.TryAdd(parameter, delayedArg);

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
                catch (Exception? e)
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

                    if (_dynamicParameterHandler.Binder != null)
                    {
                        result =
                            _dynamicParameterHandler.Binder.CoerceValidateAndBind(
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

                MergedCompiledCommandParameter? varargsParameter = null;

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
        /// <param name="inputToOperateOn">The pipeline object to bind.</param>
        /// <returns>True if binding succeeded or nothing to bind; false on error.</returns>
        internal bool BindPipelineParameters(PSObject inputToOperateOn)
            => _pipelineParameterBinder.BindPipelineParameters(inputToOperateOn);

        [DoesNotReturn]
        private void ThrowOrElaborateBindingException(ParameterBindingException ex)
        {
            if (!DefaultParameterBindingInUse)
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            ThrowElaboratedBindingException(ex);
        }

        /// <returns>
        /// The binder that should bind this parameter, or null if no binder applies.
        /// </returns>
        private ParameterBinderBase? GetBinderForParameter(MergedCompiledCommandParameter? parameter)
        {
            return parameter?.BinderAssociation switch
            {
                ParameterBinderAssociation.DeclaredFormalParameters => DefaultParameterBinder,
                ParameterBinderAssociation.CommonParameters => _commonParametersBinder,
                ParameterBinderAssociation.ShouldProcessParameters => _shouldProcessParameterBinder,
                ParameterBinderAssociation.PagingParameters => _pagingParameterBinder,
                ParameterBinderAssociation.TransactionParameters => _transactionParameterBinder,
                ParameterBinderAssociation.DynamicParameters => _dynamicParameterHandler.Binder,
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
        internal object? GetDefaultParameterValue(string name)
        {
            MergedCompiledCommandParameter? matchingParameter =
                BindableParameters.GetMatchingParameter(
                    name,
                    false,
                    true,
                    null);

            object? result = null;

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
        private readonly PipelineParameterBinder _pipelineParameterBinder;
        private readonly DelayBindScriptBlockHandler _delayBindScriptBlockHandler;
        private readonly DynamicParameterHandler _dynamicParameterHandler;
        private readonly DefaultValueManager _defaultValueManager;

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
        internal List<WarningRecord>? ObsoleteParameterWarningList { get; private set; }

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

        private HashSet<string>? _boundObsoleteParameterNames;

        private readonly ReflectionParameterBinder _commonParametersBinder;
        private readonly ReflectionParameterBinder _shouldProcessParameterBinder;
        private readonly ReflectionParameterBinder _pagingParameterBinder;
        private readonly ReflectionParameterBinder _transactionParameterBinder;

        /// <summary>
        /// A collection of the default values of the parameters.
        /// </summary>

        #endregion private_members

        protected override void SaveDefaultScriptParameterValue(string name, object value)
            => _defaultValueManager.SaveScriptParameterValue(name, value);

    }

}
