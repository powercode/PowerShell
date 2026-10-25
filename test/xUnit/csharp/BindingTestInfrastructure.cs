// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;
using Microsoft.PowerShell.Commands;

namespace PSTests.Parallel
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Class 1: StubParameterBinder
    // A concrete ParameterBinderBase that stores bound values in a dictionary
    // instead of reflecting into a real cmdlet object.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class StubParameterBinder : ParameterBinderBase
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        internal StubParameterBinder(InvocationInfo invocationInfo, ExecutionContext context)
            : base(invocationInfo, context, command: null) { }

        internal override void StoreParameterValue(
            string name,
            object value,
            CompiledCommandParameter parameterMetadata)
        {
            _values[name] = value;
        }

        internal override object GetDefaultParameterValue(string name)
        {
            _values.TryGetValue(name, out object value);
            return value;
        }

        internal IReadOnlyDictionary<string, object> BoundValues => _values;

        internal bool HasValue(string name) => _values.ContainsKey(name);

        internal object GetValue(string name) => _values[name];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Class 2: TestableParameterBinderController
    // Exposes protected algorithm methods from ParameterBinderController for
    // direct unit testing, overriding virtual dispatch to record and simulate.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class TestableParameterBinderController : ParameterBinderController
    {
        internal List<(string ParamName, object Value, uint SetFlag)> DispatchCalls { get; } = new List<(string, object, uint)>();

        internal bool DispatchResult { get; set; } = true;

        internal TestableParameterBinderController(
            InvocationInfo invocationInfo,
            ExecutionContext context,
            ParameterBinderBase parameterBinder,
            MergedCommandParameterMetadata metadata)
            : base(invocationInfo, context, parameterBinder)
        {
            _bindableParameters = metadata;
            UnboundParameters = new List<MergedCompiledCommandParameter>(
                metadata.BindableParameters.Values);
        }

        // Expose protected BindNamedParameters for direct testing
        internal new void BindNamedParameters(uint parameterSets, List<CommandParameterInternal> arguments)
            => base.BindNamedParameters(parameterSets, arguments);

        // Expose protected InitUnboundArguments for direct testing
        internal void CallInitUnboundArguments(List<CommandParameterInternal> arguments)
            => InitUnboundArguments(arguments);

        // Expose protected ReparseUnboundArguments for direct testing
        internal void CallReparseUnboundArguments()
            => ReparseUnboundArguments();

        // Expose internal BindPositionalParameters for direct testing
        internal void CallBindPositionalParameters(
            List<CommandParameterInternal> unboundArguments,
            uint validParameterSets,
            uint defaultParameterSet,
            out ParameterBindingException outgoingBindingException)
            => BindPositionalParameters(unboundArguments, validParameterSets, defaultParameterSet,
                out outgoingBindingException);

        // Expose UnboundArguments for observation
        internal List<CommandParameterInternal> GetUnboundArguments() => UnboundArguments;

        // Expose BoundParameters for observation
        internal Dictionary<string, MergedCompiledCommandParameter> GetBoundParameters() => BoundParameters;

        // Override virtual dispatch — records calls, simulates success/failure
        internal override bool DispatchBindToSubBinder(
            uint parameterSets,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
        {
            DispatchCalls.Add((parameter.Parameter.Name, argument.ArgumentValue, parameterSets));
            if (DispatchResult)
            {
                ParameterBindingState.SwapRemove(UnboundParameters, parameter);
                BoundParameters.Add(parameter.Parameter.Name, parameter);
            }

            return DispatchResult;
        }

        protected override void BindNamedParameter(
            uint parameterSets,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter)
        {
            DispatchBindToSubBinder(parameterSets, argument, parameter, ParameterBindingFlags.ShouldCoerceType);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Class 3: BindingTestFactory
    // Static factory for constructing lightweight test objects.
    // ═══════════════════════════════════════════════════════════════════════════
    internal static class BindingTestFactory
    {
        internal static (ExecutionContext Context, InvocationInfo Invocation) CreateLightweightContext()
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            PSHost host = new DefaultHost(culture, culture);
            InitialSessionState iss = InitialSessionState.CreateDefault2();
            var engine = new AutomationEngine(host, iss);
            var ctx = new ExecutionContext(engine, host, iss);
            var invocation = new InvocationInfo(
                new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);
            return (ctx, invocation);
        }

        internal static CommandParameterInternal MakeNamedArg(string paramName, object value)
        {
            return CommandParameterInternal.CreateParameterWithArgument(
                parameterAst: null,
                parameterName: paramName,
                parameterText: $"-{paramName}:",
                argumentAst: null,
                value: value,
                spaceAfterParameter: false);
        }

        internal static CommandParameterInternal MakePositionalArg(object value)
        {
            return CommandParameterInternal.CreateArgument(value);
        }

        internal static CommandParameterInternal MakeSplattedNamedArg(string paramName, object value)
        {
            return CommandParameterInternal.CreateParameterWithArgument(
                parameterAst: null,
                parameterName: paramName,
                parameterText: $"-{paramName}:",
                argumentAst: null,
                value: value,
                spaceAfterParameter: false,
                fromSplatting: true);
        }

        internal static CommandParameterInternal MakeSwitchArg(string paramName)
        {
            return CommandParameterInternal.CreateParameter(
                parameterName: paramName,
                parameterText: $"-{paramName}");
        }

        internal static MergedCommandParameterMetadata BuildMetadata(params RuntimeDefinedParameter[] parameters)
        {
            return ParameterSetResolverTestFactory.BuildMetadata(parameters);
        }

        internal static TestableParameterBinderController CreateController(
            MergedCommandParameterMetadata metadata)
        {
            var (ctx, invocation) = CreateLightweightContext();
            var binder = new StubParameterBinder(invocation, ctx);
            return new TestableParameterBinderController(invocation, ctx, binder, metadata);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Class 4: RichTestBindingContext
    // Enhanced binding context implementing IBindingStateContext and
    // IBindingOperationsContext with configurable dispatch and observation helpers.
    // Used by pipeline/delay-bind/dynamic-param unit tests.
    // ═══════════════════════════════════════════════════════════════════════════
#pragma warning disable CA1812 // Instantiated by pipeline and delay-bind unit tests defined in separate files
    internal sealed class RichTestBindingContext : IBindingStateContext, IBindingOperationsContext
    {
#pragma warning restore CA1812

        // ── IBindingStateContext ─────────────────────────────────────────────

        public IList<MergedCompiledCommandParameter> UnboundParameters { get; set; }
            = new List<MergedCompiledCommandParameter>();

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; set; }
            = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandParameterInternal> BoundArguments { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        public List<CommandParameterInternal> UnboundArguments { get; set; }
            = new List<CommandParameterInternal>();

        public List<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; }
            = new List<MergedCompiledCommandParameter>();

        public InvocationInfo InvocationInfo { get; set; }
            = new InvocationInfo(new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);

        public ParameterSetResolver ParameterSetResolver { get; set; } = null;

        public uint CurrentParameterSetFlag { get; set; } = uint.MaxValue;

        public uint DefaultParameterSetFlag { get; set; } = 0;

        public string DefaultParameterSetName { get; } = string.Empty;

        public string CommandName { get; set; } = string.Empty;

        public bool ImplementsDynamicParameters { get; } = false;

        public Cmdlet Command { get; } = null;

        public ExecutionContext Context { get; } = null;

        public MergedCommandParameterMetadata BindableParameters { get; set; } = null;

        public CommandLineParameters CommandLineParameters { get; } = null;

        public bool DefaultParameterBindingInUse { get; set; } = false;

        public List<string> BoundDefaultParameters { get; } = new List<string>();

        public List<string> DefaultParameterAliasList { get; set; } = null;

        public HashSet<string> DefaultParameterWarningSet { get; } = new HashSet<string>();

        public Dictionary<MergedCompiledCommandParameter, object> AllDefaultParameterValuePairs { get; set; } = null;

        public bool UseDefaultParameterBinding { get; set; } = false;

        public Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; }
            = new Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument>();

        public Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        // ── Configuration and observation ───────────────────────────────────

        internal Func<CommandParameterInternal, MergedCompiledCommandParameter, bool> OnDispatchBind { get; set; }

        internal Func<PSObject, (bool Result, bool ThereWasSomethingToBind)> OnInvokeDelayBind { get; set; }

        internal bool ApplyDefaultParameterBindingResult { get; set; } = false;

        internal List<(string ParamName, object Value)> BindCalls { get; } = new List<(string, object)>();

        internal List<MergedCompiledCommandParameter> RestoredParameters { get; } = new List<MergedCompiledCommandParameter>();

        internal List<MergedCompiledCommandParameter> BackedUpParameters { get; } = new List<MergedCompiledCommandParameter>();

        internal string LastSetName { get; private set; }

        internal ParameterBindingException LastException { get; private set; }

        // ── IBindingOperationsContext ────────────────────────────────────────

        public void SetParameterSetName(string parameterSetName)
            => LastSetName = parameterSetName;

        public void ThrowOrElaborateBindingException(ParameterBindingException exception)
        {
            LastException = exception;
            throw exception;
        }

        public bool DispatchBindToSubBinder(
            uint validParameterSetFlag,
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
        {
            BindCalls.Add((parameter.Parameter.Name, argument.ArgumentValue));
            if (OnDispatchBind != null)
            {
                return OnDispatchBind(argument, parameter);
            }

            // Default: simulate successful bind
            UnboundParameters.Remove(parameter);
            BoundParameters[parameter.Parameter.Name] = parameter;
            BoundArguments[parameter.Parameter.Name] = argument;
            return true;
        }

        public bool BindToAssociatedBinder(
            CommandParameterInternal argument,
            MergedCompiledCommandParameter parameter,
            ParameterBindingFlags flags)
            => false;

        public bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags)
            => false;

        public void ReparseUnboundArguments() { }

        public void BindNamedParameters(uint parameterSetFlag, List<CommandParameterInternal> args) { }

        public void BindPositionalParameters(
            List<CommandParameterInternal> args,
            uint currentParameterSetFlag,
            uint defaultParameterSetFlag,
            out ParameterBindingException outgoingBindingException)
        {
            outgoingBindingException = null;
        }

        public IScriptExtent GetErrorExtent(CommandParameterInternal argument) => null;

        public object GetDefaultParameterValue(string name) => null;

        public bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter)
        {
            RestoredParameters.Add(parameter);
            return true;
        }

        public HashSet<string> CopyBoundPositionalParameters() => new HashSet<string>();

        public bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind)
        {
            if (OnInvokeDelayBind != null)
            {
                var value = OnInvokeDelayBind(inputToOperateOn);
                thereWasSomethingToBind = value.ThereWasSomethingToBind;
                return value.Result;
            }

            thereWasSomethingToBind = false;
            return false;
        }

        public void BackupDefaultParameter(MergedCompiledCommandParameter parameter)
            => BackedUpParameters.Add(parameter);

        public void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters)
        {
            foreach (var p in parameters)
            {
                RestoredParameters.Add(p);
            }
        }

        public CommandParameterInternal RentPipelineCpi()
            => CommandParameterInternal.CreateArgument(value: null);

        public void ReturnPipelineCpi(CommandParameterInternal cpi) { }

        public bool ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag)
            => ApplyDefaultParameterBindingResult;

        internal void InitializePipelineState(
            MergedCommandParameterMetadata metadata,
            string defaultParameterSetName = null)
        {
            BindableParameters = metadata;
            UnboundParameters = new List<MergedCompiledCommandParameter>(metadata.BindableParameters.Values);
            BoundParameters.Clear();
            BoundArguments.Clear();
            ParametersBoundThroughPipelineInput.Clear();

            var commandMetadata = new CommandMetadata(typeof(PSCmdlet));
            if (!string.IsNullOrEmpty(defaultParameterSetName))
            {
                commandMetadata.DefaultParameterSetName = defaultParameterSetName;
                commandMetadata.DefaultParameterSetFlag = metadata.GenerateParameterSetMappingFromMetadata(defaultParameterSetName);
                DefaultParameterSetFlag = commandMetadata.DefaultParameterSetFlag;
            }

            ParameterSetResolver = new ParameterSetResolver(commandMetadata, metadata, this, this)
            {
                CurrentParameterSetFlag = uint.MaxValue,
                PrePipelineProcessingParameterSetFlags = uint.MaxValue,
            };

            CommandName = commandMetadata.Name;
        }
    }
}
