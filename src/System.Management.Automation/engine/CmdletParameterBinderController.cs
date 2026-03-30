// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace System.Management.Automation
{
    /// <summary>
    /// This is the interface between the CommandProcessor and the various
    /// parameter binders required to bind parameters to a cmdlet.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal class CmdletParameterBinderController : ParameterBinderController, IBindingStateContext, IBindingOperationsContext
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
        internal CmdletParameterBinderController(Cmdlet cmdlet, CommandMetadata commandMetadata, ParameterBinderBase parameterBinder)
            : base(cmdlet.MyInvocation, cmdlet.Context, parameterBinder)
        {
            PSTraceSource.ThrowIfNull(cmdlet);
            PSTraceSource.ThrowIfNull(commandMetadata);

            Command = cmdlet;
            _commandRuntime = (MshCommandRuntime)cmdlet.CommandRuntime;
            _commandMetadata = commandMetadata;

            _commonParametersBinder = new ReflectionParameterBinder(new CommonParameters(_commandRuntime), cmdlet, CommandLineParameters);
            _shouldProcessParameterBinder = new ReflectionParameterBinder(new ShouldProcessParameters(_commandRuntime), cmdlet, CommandLineParameters);
            _pagingParameterBinder = new ReflectionParameterBinder(new PagingParameters(_commandRuntime), cmdlet, CommandLineParameters);
            _transactionParameterBinder = new ReflectionParameterBinder(new TransactionParameters(_commandRuntime), cmdlet, CommandLineParameters);

            // Record the command name in the binding state for debugger display.
            State.CommandName = commandMetadata.Name;

            // Resolve the initial unbound parameter list before renting BindingState,
            // because RentBindingState.Reset() will populate UnboundParameters from it.
            List<MergedCompiledCommandParameter> initialParameters;
            if (commandMetadata.ImplementsDynamicParameters)
            {
                // ReplaceMetadata makes a copy for us, so we can use that collection as is.
                initialParameters = BindableParameters.ReplaceMetadata(commandMetadata.StaticCommandParameterMetadata);
            }
            else
            {
                _bindableParameters = commandMetadata.StaticCommandParameterMetadata;

                // Must make a copy of the list because we'll modify it.
                initialParameters = new List<MergedCompiledCommandParameter>(_bindableParameters.BindableParameters.Values);
            }

            // Rent a BindingState from the per-runspace pool (or allocate a new one if the pool
            // is empty). This overwrites the default State allocated by ParameterBinderController.
            State = cmdlet.Context.RentBindingState(initialParameters, commandMetadata.Name);

            // In DEBUG builds, assert that the rented state is fully clean.
            State.AssertClean(initialParameters.Count);

            ParameterSetResolver = new ParameterSetResolver(_commandMetadata, BindableParameters, stateContext: this, opsContext: this);

            _defaultParameterValueBinder = new DefaultParameterValueBinder(_commandMetadata, _commandRuntime, cmdlet.Context, BindableParameters, stateContext: this, opsContext: this);

            _mandatoryParameterPrompter = new MandatoryParameterPrompter(ParameterSetResolver, Context, Command, opsContext: this);

            _pipelineParameterBinder = new PipelineParameterBinder(this, this);
            _delayBindScriptBlockHandler = new DelayBindScriptBlockHandler(this, this);
            _dynamicParameterHandler = new DynamicParameterHandler(this, this);
            _defaultValueManager = new DefaultValueManager(this, this);
        }

        #endregion ctor

        /// <summary>
        /// Returns the rented <see cref="ParameterBindingState"/> to the per-runspace pool on
        /// <see cref="ExecutionContext"/> so it can be reused by the next command invocation.
        /// Called from <see cref="CommandProcessor"/> during disposal.
        /// </summary>
        internal void ReturnState() => Context.ReturnBindingState(State);

        private string DebuggerDisplayValue
        {
            get
            {
                string commandName = InvocationInfo.MyCommand?.Name ?? "(unknown)";
                uint setFlag = ParameterSetResolver.CurrentParameterSetFlag;
                return $"CmdletBinder: {commandName}, ParamSet=0x{setFlag:X8}";
            }
        }

        #region helper_methods

        /// <summary>
        /// Binds the specified command-line parameters to the target.
        /// </summary>
        /// <remarks>
        /// The command-line binding orchestration executes these high-level phases in order:
        /// <list type="number">
        ///   <item><description>Run non-validating command-line binding (<see cref="BindCommandLineParametersNoValidation(List{CommandParameterInternal})"/>).</description></item>
        ///   <item><description>Determine whether pipeline input is expected for this invocation.</description></item>
        ///   <item><description>Validate and narrow candidate parameter sets for strict execution mode.</description></item>
        ///   <item><description>Re-apply default parameter binding for mandatory-check scenarios when a single set is selected.</description></item>
        ///   <item><description>If pipeline input is expected and multiple sets remain, filter out sets that cannot take pipeline input.</description></item>
        ///   <item><description>Handle unbound mandatory parameters (prompt or throw as appropriate for command visibility and mode).</description></item>
        ///   <item><description>Bind remaining unbound script parameters for script binder scenarios.</description></item>
        ///   <item><description>If no more pipeline input is expected, verify that a single parameter set is selected.</description></item>
        ///   <item><description>Persist pre-pipeline parameter set flags for per-input-object pipeline rebinding.</description></item>
        /// </list>
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
        internal void BindCommandLineParameters(List<CommandParameterInternal> arguments)
        {
            s_tracer.WriteLine("Argument count: {0}", arguments.Count);

            BindCommandLineParametersNoValidation(arguments);

            bool isPipelineInputExpected = !(_commandRuntime.IsClosed && _commandRuntime.InputPipe.Empty);

            int validParameterSetCount = DetermineValidParameterSetCount(isPipelineInputExpected);

            TryApplyDefaultsForMandatoryCheck(validParameterSetCount);

            if (isPipelineInputExpected)
            {
                validParameterSetCount = FilterParameterSetsForPipelineInput(validParameterSetCount);
            }

            HandleMandatoryParameterCheck(validParameterSetCount, isPipelineInputExpected);

            if (!isPipelineInputExpected)
            {
                ParameterSetResolver.VerifyParameterSetSelected();
            }

            ParameterSetResolver.PrePipelineProcessingParameterSetFlags = ParameterSetResolver.CurrentParameterSetFlag;
        }

        // Returns the number of valid parameter sets based on whether pipeline input is expected.
        // When pipeline input is not expected, ValidateParameterSets is called with strict mode
        // so mandatory checks fire immediately.
        private int DetermineValidParameterSetCount(bool isPipelineInputExpected)
        {
            if (!isPipelineInputExpected)
            {
                return ParameterSetResolver.ValidateParameterSets(false, true, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
            }

            return ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
        }

        // If a single parameter set has been determined and $PSDefaultParameterValues has not yet
        // been applied, re-apply it now so that mandatory parameters it supplies are visible
        // during the upcoming mandatory-parameter check.
        private void TryApplyDefaultsForMandatoryCheck(int validParameterSetCount)
        {
            if (validParameterSetCount == 1 && !DefaultParameterBindingInUse)
            {
                if (_defaultParameterValueBinder.ApplyDefaultParameterBinding("Mandatory Checking", isDynamic: false,
                    currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
                {
                    DefaultParameterBindingInUse = true;
                }
            }
        }

        // Removes parameter sets that cannot accept pipeline input when more than one set is
        // still in play, then re-validates to get the updated count.
        private int FilterParameterSetsForPipelineInput(int validParameterSetCount)
        {
            if (validParameterSetCount > 1)
            {
                uint filteredValidParameterSetFlags = ParameterSetResolver.FilterParameterSetsTakingNoPipelineInput(_delayBindScriptBlockHandler.Keys);
                if (filteredValidParameterSetFlags != ParameterSetResolver.CurrentParameterSetFlag)
                {
                    ParameterSetResolver.CurrentParameterSetFlag = filteredValidParameterSetFlags;
                    validParameterSetCount = ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);
                }
            }

            return validParameterSetCount;
        }

        // Prompts for or throws on any unbound mandatory parameters, then binds remaining
        // unbound script parameters for script-binder scenarios.
        private void HandleMandatoryParameterCheck(int validParameterSetCount, bool isPipelineInputExpected)
        {
            using (ParameterBinderBase.bindingTracer.TraceScope("MANDATORY PARAMETER CHECK on cmdlet [{0}]", _commandMetadata.Name))
            {
                try
                {
                    
                    // We shouldn't prompt for mandatory parameters if this command is private.
                    bool promptForMandatoryParameters = Command.CommandInfo.Visibility == SessionStateEntryVisibility.Public;

                    // The missingMandatoryParameters out parameter is used for error reporting when
                    // binding from the pipeline. We're not binding from the pipeline here: if a
                    // mandatory non-pipeline parameter is missing it will be prompted for or an
                    // exception will be raised, so we can ignore the out parameter.                    
                    _mandatoryParameterPrompter.HandleUnboundMandatoryParameters(validParameterSetCount, true, promptForMandatoryParameters, isPipelineInputExpected, out _);

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
        }

        /// <summary>
        /// Binds the unbound arguments to parameters but does not
        /// perform mandatory parameter validation or parameter set validation.
        /// </summary>
        /// <remarks>
        /// The non-validating binding pipeline executes these phases in order:
        /// <list type="number">
        ///   <item><description>Initialize unbound arguments from command-line tokens.</description></item>
        ///   <item><description>Parse $PSDefaultParameterValues for this command.</description></item>
        ///   <item><description>Re-pair parameter names with argument values (<see cref="ParameterBinderController.ReparseUnboundArguments"/>).</description></item>
        ///   <item><description>Bind named parameters with exact name-to-parameter matching.</description></item>
        ///   <item>
        ///     <description>Bind positional parameters with the 4-pass algorithm:</description>
        ///     <list type="bullet">
        ///       <item><description>Default parameter set, no type coercion.</description></item>
        ///       <item><description>Other parameter sets, no type coercion.</description></item>
        ///       <item><description>Default parameter set, with type coercion.</description></item>
        ///       <item><description>Other parameter sets, with type coercion.</description></item>
        ///     </list>
        ///   </item>
        ///   <item><description>Apply $PSDefaultParameterValues after positional binding.</description></item>
        ///   <item><description>Validate that at least one parameter set remains resolvable.</description></item>
        ///   <item><description>Handle dynamic parameters and re-run matching with expanded metadata.</description></item>
        ///   <item><description>Re-apply $PSDefaultParameterValues after dynamic binding.</description></item>
        ///   <item><description>Bind ValueFromRemainingArguments parameters.</description></item>
        ///   <item><description>Verify all command-line arguments were consumed.</description></item>
        /// </list>
        /// </remarks>
        internal void BindCommandLineParametersNoValidation(List<CommandParameterInternal> arguments)
        {
            PrepareForBinding(arguments);

            BindNamedCommandLineParameters();

            ParameterBindingException? reportedBindingException = BindPositionalCommandLineParameters();

            TryApplyDefaultsAfterPositionalBind();

            ParameterSetResolver.ValidateParameterSets(true, false, ParameterSetResolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput);

            ParameterBindingException? dynamicBindingException = BindDynamicParameters();

            TryApplyDefaultsAfterDynamicBind();

            reportedBindingException ??= dynamicBindingException;

            HandleRemainingArguments();

            VerifyArgumentsProcessed(reportedBindingException);
        }

        // Initializes binding state: prepares any script cmdlet, loads command-line tokens into
        // UnboundArguments, parses $PSDefaultParameterValues, and re-pairs names with values.
        private void PrepareForBinding(List<CommandParameterInternal> arguments)
        {
            (Command as PSScriptCmdlet)?.PrepareForBinding(CommandLineParameters);

            InitUnboundArguments(arguments);

            _defaultParameterValueBinder.ResetForNewBinding();
            _defaultParameterValueBinder.GetDefaultParameterValuePairs(true);
            DefaultParameterBindingInUse = false;
            BoundDefaultParameters.Clear();

            ReparseUnboundArguments();
        }

        // Binds named parameters (exact name-to-parameter matching) from the command line.
        private void BindNamedCommandLineParameters()
        {
            using (ParameterBinderBase.bindingTracer.TraceScope("BIND NAMED cmd line args [{0}]", _commandMetadata.Name))
            {
                BindNamedParameters(ParameterSetResolver.CurrentParameterSetFlag, UnboundArguments);
            }
        }

        // Runs the 4-pass positional binding algorithm and returns any binding exception to carry
        // forward for later error reporting.
        private ParameterBindingException? BindPositionalCommandLineParameters()
        {
            using (ParameterBinderBase.bindingTracer.TraceScope("BIND POSITIONAL cmd line args [{0}]", _commandMetadata.Name))
            {
                BindPositionalParameters(UnboundArguments, ParameterSetResolver.CurrentParameterSetFlag,
                    _commandMetadata.DefaultParameterSetFlag, out ParameterBindingException? bindingException);

                return bindingException;
            }
        }

        // Applies $PSDefaultParameterValues after positional binding so that default values can
        // influence parameter-set selection before the default set is applied.
        private void TryApplyDefaultsAfterPositionalBind()
        {
            if (_defaultParameterValueBinder.ApplyDefaultParameterBinding(
                "POSITIONAL BIND",
                isDynamic: false,
                currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
            {
                DefaultParameterBindingInUse = true;
            }
        }

        // Expands dynamic parameters and binds them. Returns any binding exception generated
        // during dynamic parameter handling for later error reporting.
        private ParameterBindingException? BindDynamicParameters()
        {
            _dynamicParameterHandler.Handle(out ParameterBindingException? bindingException);
            return bindingException;
        }

        // Re-applies $PSDefaultParameterValues after dynamic binding so that newly added
        // parameter metadata can be matched against default values.
        private void TryApplyDefaultsAfterDynamicBind()
        {
            if (_defaultParameterValueBinder.ApplyDefaultParameterBinding("DYNAMIC BIND", isDynamic: true, currentParameterSetFlag: ParameterSetResolver.CurrentParameterSetFlag))
            {
                DefaultParameterBindingInUse = true;
            }
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
        protected override void BindNamedParameter(uint parameterSets, CommandParameterInternal argument, MergedCompiledCommandParameter parameter)
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

            if (TryDelayBindScriptBlock(parameterSets, argument, parameter, flags))
            {
                return false;
            }

            try
            {
                return BindToAssociatedBinder(argument, parameter, flags);
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

            return false;
        }

        /// <summary>
        /// Checks whether <paramref name="argument"/> is a ScriptBlock that must be delay-bound
        /// until a pipeline object is available. When it is, records the delayed binding and
        /// returns <see langword="true"/> so the caller skips normal binding.
        /// </summary>
        private bool TryDelayBindScriptBlock(
            uint parameterSets,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
        {
            if ((flags & ParameterBindingFlags.DelayBindScriptBlock) == 0 ||
                !parameter.Parameter.DoesParameterSetTakePipelineInput(parameterSets) ||
                !argument.ArgumentSpecified)
            {
                return false;
            }

            object argumentValue = argument.ArgumentValue;
            if (!(argumentValue is ScriptBlock || argumentValue is DelayBindScriptBlockHandler.DelayedScriptBlockArgument) ||
                IsParameterScriptBlockBindable(parameter))
            {
                return false;
            }

            // If the command expects no pipeline input, throw now so the user gets a better
            // error than "unbound mandatory parameter".
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

            // We need to delay binding of this argument to the parameter.
            // The script block is run for each pipeline object and its result is bound.
            DelayBindScriptBlockHandler.DelayedScriptBlockArgument delayedArg =
                argumentValue as DelayBindScriptBlockHandler.DelayedScriptBlockArgument
                ?? _delayBindScriptBlockHandler.CreateEntry(argument);
            _delayBindScriptBlockHandler.TryAdd(parameter, delayedArg);

            if (parameter.Parameter.ParameterSetFlags != 0)
            {
                ParameterSetResolver.NarrowByParameterSetFlags(parameter.Parameter.ParameterSetFlags);
            }

            ParameterBindingState.SwapRemove(UnboundParameters, parameter);

            BoundParameters[parameter.Parameter.Name] = parameter;
            BoundArguments[parameter.Parameter.Name] = argument;

            if (DefaultParameterBinder.RecordBoundParameters &&
                !DefaultParameterBinder.CommandLineParameters.ContainsKey(parameter.Parameter.Name))
            {
                DefaultParameterBinder.CommandLineParameters.Add(parameter.Parameter.Name, delayedArg);
            }

            return true;
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
        private bool BindToAssociatedBinder(CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags)
        {
            bool result = false;

            switch (parameter.BinderAssociation)
            {
                case ParameterBinderAssociation.DeclaredFormalParameters:
                    result = DefaultParameterBinder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    break;

                case ParameterBinderAssociation.CommonParameters:
                    result = _commonParametersBinder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    break;

                case ParameterBinderAssociation.ShouldProcessParameters:
                    Diagnostics.Assert(_commandMetadata.SupportsShouldProcess,
                        "The metadata for the ShouldProcessParameters should only be available if the command supports ShouldProcess");

                    result = _shouldProcessParameterBinder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    break;

                case ParameterBinderAssociation.PagingParameters:
                    Diagnostics.Assert(_commandMetadata.SupportsPaging, "The metadata for the PagingParameters should only be available if the command supports paging");

                    result = _pagingParameterBinder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    break;

                case ParameterBinderAssociation.TransactionParameters:
                    Diagnostics.Assert(_commandMetadata.SupportsTransactions,
                        "The metadata for the TransactionsParameters should only be available if the command supports transactions");

                    result = _transactionParameterBinder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    break;

                case ParameterBinderAssociation.DynamicParameters:
                    Diagnostics.Assert(_commandMetadata.ImplementsDynamicParameters, 
                        "The metadata for the dynamic parameters should only be available if the command supports IDynamicParameters");

                    if (_dynamicParameterHandler.Binder != null)
                    {
                        result = _dynamicParameterHandler.Binder.CoerceValidateAndBind(argument, parameter.Parameter, flags);
                    }

                    break;
            }

            if (result && ((flags & ParameterBindingFlags.IsDefaultValue) == 0))
            {
                RecordSuccessfulBind(parameter, argument, flags);
            }

            return result;
        }

        /// <summary>
        /// Records a successful parameter binding: narrows the valid parameter sets, moves the
        /// parameter from unbound to bound, and queues an obsolete warning when applicable.
        /// </summary>
        private void RecordSuccessfulBind(MergedCompiledCommandParameter parameter, CommandParameterInternal argument, ParameterBindingFlags flags)
        {
            if (parameter.Parameter.ParameterSetFlags != 0)
            {
                ParameterSetResolver.NarrowByParameterSetFlags(parameter.Parameter.ParameterSetFlags);
            }

            ParameterBindingState.SwapRemove(UnboundParameters, parameter);

            BoundParameters.TryAdd(parameter.Parameter.Name, parameter);

            BoundArguments.TryAdd(parameter.Parameter.Name, argument);

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

                State.ObsoleteParameterWarningList ??= new List<WarningRecord>();

                State.ObsoleteParameterWarningList.Add(warningRecord);
            }
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
                    BindVarargsArguments(varargsParameter);
                }
            }
        }

        /// <summary>
        /// Builds the combined argument list from all remaining unbound arguments and binds it
        /// to the given <c>ValueFromRemainingArguments</c> parameter.
        /// </summary>
        private void BindVarargsArguments(MergedCompiledCommandParameter varargsParameter)
        {
            using (ParameterBinderBase.bindingTracer.TraceScope("BIND REMAININGARGUMENTS cmd line args to param: [{0}]", varargsParameter.Parameter.Name))
            {
                // Accumulate the unbound arguments into a list and then bind it to the parameter.
                List<object> valueFromRemainingArguments = new();

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
                string parameterName = "-" + varargsParameter.Parameter.Name + ":";
                var cpi = CommandParameterInternal.CreateParameterWithArgument(parameterAst: null, varargsParameter.Parameter.Name, parameterName,
                    argumentAst, valueFromRemainingArguments, spaceAfterParameter: false);

                // To make all the following work similarly (the first is handled elsewhere, but second and third are
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

        /// <summary>
        /// Checks for unbound mandatory parameters. If any are found, an exception is thrown.
        /// </summary>
        /// <param name="missingMandatoryParameters">
        /// Returns the missing mandatory parameters, if any.
        /// </param>
        /// <returns>
        /// True if there are no unbound mandatory parameters. False if there are unbound mandatory parameters.
        /// </returns>
        internal bool HandleUnboundMandatoryParameters(out List<MergedCompiledCommandParameter> missingMandatoryParameters)
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
                BindableParameters.GetMatchingParameter(name, throwOnParameterNotFound: false, tryExactMatching: true, invocationInfo: null);

            object? result = null;

            try
            {
                result = GetBinderForParameter(matchingParameter)?.GetDefaultParameterValue(name);
            }
            catch (GetValueException getValueException)
            {
                ParameterBindingParameterDefaultValueException.ThrowGetDefaultValueFailed(getValueException, Command.MyInvocation, name, getValueException.Message);
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
        /// Routes through <see cref="ParameterBindingState"/> for pooled allocation.
        /// </summary>
        internal List<WarningRecord>? ObsoleteParameterWarningList => State.ObsoleteParameterWarningList;

        /// <summary>
        /// Keep names of the parameters for which we have generated obsolete warning messages.
        /// </summary>
        private HashSet<string> BoundObsoleteParameterNames => State.BoundObsoleteParameterNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ReflectionParameterBinder _commonParametersBinder;
        private readonly ReflectionParameterBinder _shouldProcessParameterBinder;
        private readonly ReflectionParameterBinder _pagingParameterBinder;
        private readonly ReflectionParameterBinder _transactionParameterBinder;

        /// <summary>
        /// A collection of the default values of the parameters.
        /// </summary>

        #endregion private_members

        #region IBindingStateContext

        IList<MergedCompiledCommandParameter> IBindingStateContext.UnboundParameters => UnboundParameters;

        Dictionary<string, MergedCompiledCommandParameter> IBindingStateContext.BoundParameters => BoundParameters;

        Dictionary<string, CommandParameterInternal> IBindingStateContext.BoundArguments => BoundArguments;

        List<CommandParameterInternal> IBindingStateContext.UnboundArguments
        {
            get => UnboundArguments;
            set => UnboundArguments = value;
        }

        List<MergedCompiledCommandParameter> IBindingStateContext.ParametersBoundThroughPipelineInput
            => ParametersBoundThroughPipelineInput;

        InvocationInfo IBindingStateContext.InvocationInfo => Command.MyInvocation;

        ParameterSetResolver IBindingStateContext.ParameterSetResolver => ParameterSetResolver;

        uint IBindingStateContext.CurrentParameterSetFlag => ParameterSetResolver.CurrentParameterSetFlag;

        uint IBindingStateContext.DefaultParameterSetFlag
        {
            get => _commandMetadata.DefaultParameterSetFlag;
            set => _commandMetadata.DefaultParameterSetFlag = value;
        }

        string IBindingStateContext.DefaultParameterSetName => _commandMetadata.DefaultParameterSetName;

        string IBindingStateContext.CommandName => _commandMetadata.Name;

        bool IBindingStateContext.ImplementsDynamicParameters => _commandMetadata.ImplementsDynamicParameters;

        Cmdlet IBindingStateContext.Command => Command;

        ExecutionContext IBindingStateContext.Context => Context;

        MergedCommandParameterMetadata IBindingStateContext.BindableParameters => BindableParameters;

        CommandLineParameters IBindingStateContext.CommandLineParameters => CommandLineParameters;

        bool IBindingStateContext.DefaultParameterBindingInUse
        {
            get => DefaultParameterBindingInUse;
            set => DefaultParameterBindingInUse = value;
        }

        List<string> IBindingStateContext.BoundDefaultParameters => BoundDefaultParameters;

        List<string>? IBindingStateContext.DefaultParameterAliasList
        {
            get => State.DefaultParameterAliasList;
            set => State.DefaultParameterAliasList = value;
        }

        HashSet<string> IBindingStateContext.DefaultParameterWarningSet => State.DefaultParameterWarningSet;

        Dictionary<MergedCompiledCommandParameter, object>? IBindingStateContext.AllDefaultParameterValuePairs
        {
            get => State.AllDefaultParameterValuePairs;
            set => State.AllDefaultParameterValuePairs = value;
        }

        bool IBindingStateContext.UseDefaultParameterBinding
        {
            get => State.UseDefaultParameterBinding;
            set => State.UseDefaultParameterBinding = value;
        }

        Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> IBindingStateContext.DelayBindScriptBlocks
            => State.DelayBindScriptBlocks;

        Dictionary<string, CommandParameterInternal> IBindingStateContext.DefaultParameterValues => State.DefaultParameterValues;

        #endregion IBindingStateContext

        #region IBindingOperationsContext

        void IBindingOperationsContext.SetParameterSetName(string parameterSetName) => Command.SetParameterSetName(parameterSetName);

        void IBindingOperationsContext.ThrowOrElaborateBindingException(ParameterBindingException exception) => ThrowOrElaborateBindingException(exception);

        bool IBindingOperationsContext.DispatchBindToSubBinder(
            uint validParameterSetFlag,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
            => DispatchBindToSubBinder(validParameterSetFlag, argument, parameter, flags);

        bool IBindingOperationsContext.BindToAssociatedBinder(CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags)
            => BindToAssociatedBinder(argument, parameter, flags);

        bool IBindingOperationsContext.ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags)
            => ResolveAndBindNamedParameter(argument, flags);

        void IBindingOperationsContext.ReparseUnboundArguments() => ReparseUnboundArguments();

        void IBindingOperationsContext.BindNamedParameters(uint parameterSetFlag, List<CommandParameterInternal> args)
            => BindNamedParameters(parameterSetFlag, args);

        void IBindingOperationsContext.BindPositionalParameters(
            List<CommandParameterInternal> args,
            uint currentParameterSetFlag,
            uint defaultParameterSetFlag,
            out ParameterBindingException? outgoingBindingException)
            => BindPositionalParameters(args, currentParameterSetFlag, defaultParameterSetFlag, out outgoingBindingException);

        IScriptExtent IBindingOperationsContext.GetErrorExtent(CommandParameterInternal argument) => GetErrorExtent(argument);

        object? IBindingOperationsContext.GetDefaultParameterValue(string name) => GetDefaultParameterValue(name);

        bool IBindingOperationsContext.RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter)
            => RestoreParameter(argument, parameter);

        HashSet<string> IBindingOperationsContext.CopyBoundPositionalParameters()
            => DefaultParameterBinder.CommandLineParameters.CopyBoundPositionalParameters();

        bool IBindingOperationsContext.InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind)
            => _delayBindScriptBlockHandler.InvokeAndBind(inputToOperateOn, out thereWasSomethingToBind);

        void IBindingOperationsContext.BackupDefaultParameter(MergedCompiledCommandParameter parameter)
            => _defaultValueManager.Backup(parameter);

        void IBindingOperationsContext.RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters)
            => _defaultValueManager.Restore(parameters);

        CommandParameterInternal IBindingOperationsContext.RentPipelineCpi()
            => State.RentPipelineCpi();

        void IBindingOperationsContext.ReturnPipelineCpi(CommandParameterInternal cpi) => State.ReturnPipelineCpi(cpi);

        bool IBindingOperationsContext.ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag)
            => _defaultParameterValueBinder.ApplyDefaultParameterBinding(caller, isDynamic, currentParameterSetFlag);

        #endregion IBindingOperationsContext

        protected override void SaveDefaultScriptParameterValue(string name, string parameterText, object value)
            => _defaultValueManager.SaveScriptParameterValue(name, parameterText, value);

    }

}
