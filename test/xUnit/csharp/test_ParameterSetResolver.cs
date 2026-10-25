// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation.Language;
using Microsoft.PowerShell.Commands;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Lightweight stub implementing the binding context interfaces for resolver tests.
    /// </summary>
    internal sealed class TestBindingContext : IBindingStateContext, IBindingOperationsContext
    {
        // IBindingStateContext — only the subset used by ParameterSetResolver is populated with real values
        public IList<MergedCompiledCommandParameter> UnboundParameters { get; set; } = new List<MergedCompiledCommandParameter>();

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; set; } = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandParameterInternal> BoundArguments { get; } = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        public List<CommandParameterInternal> UnboundArguments { get; set; } = new List<CommandParameterInternal>();

        public List<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; } = new List<MergedCompiledCommandParameter>();

        public InvocationInfo InvocationInfo { get; set; } = new InvocationInfo(new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);

        public ParameterSetResolver ParameterSetResolver { get; } = null;

        public uint CurrentParameterSetFlag { get; } = 0;

        public uint DefaultParameterSetFlag { get; set; } = 0;

        public string DefaultParameterSetName { get; } = string.Empty;

        public string CommandName { get; } = string.Empty;

        public bool ImplementsDynamicParameters { get; } = false;

        public Cmdlet Command { get; } = null;

        public ExecutionContext Context { get; } = null;

        public MergedCommandParameterMetadata BindableParameters { get; } = null;

        public CommandLineParameters CommandLineParameters { get; } = null;

        public bool DefaultParameterBindingInUse { get; set; } = false;

        public List<string> BoundDefaultParameters { get; } = new List<string>();

        public List<string> DefaultParameterAliasList { get; set; } = null;

        public HashSet<string> DefaultParameterWarningSet { get; } = new HashSet<string>();

        public Dictionary<MergedCompiledCommandParameter, object> AllDefaultParameterValuePairs { get; set; } = null;

        public bool UseDefaultParameterBinding { get; set; } = false;

        public Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; } = new Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument>();

        public Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; } = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        // Test observation properties
        public string LastSetName { get; private set; }

        public ParameterBindingException LastException { get; private set; }

        // IBindingOperationsContext — only SetParameterSetName/ThrowOrElaborateBindingException used in tests
        public void SetParameterSetName(string parameterSetName) { LastSetName = parameterSetName; }

        public void ThrowOrElaborateBindingException(ParameterBindingException exception) { LastException = exception; throw exception; }

        public bool DispatchBindToSubBinder(uint validParameterSetFlag, CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags) => false;

        public bool BindToAssociatedBinder(CommandParameterInternal argument, MergedCompiledCommandParameter parameter, ParameterBindingFlags flags) => false;

        public bool ResolveAndBindNamedParameter(CommandParameterInternal argument, ParameterBindingFlags flags) => false;

        public void ReparseUnboundArguments() { }

        public void BindNamedParameters(uint parameterSetFlag, List<CommandParameterInternal> args) { }

        public void BindPositionalParameters(List<CommandParameterInternal> args, uint currentParameterSetFlag, uint defaultParameterSetFlag, out ParameterBindingException outgoingBindingException) { outgoingBindingException = null; }

        public IScriptExtent GetErrorExtent(CommandParameterInternal argument) => null;

        public object GetDefaultParameterValue(string name) => null;

        public bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter) => false;

        public HashSet<string> CopyBoundPositionalParameters() => new HashSet<string>();

        public bool InvokeAndBindDelayBindScriptBlock(PSObject inputToOperateOn, out bool thereWasSomethingToBind) { thereWasSomethingToBind = false; return false; }

        public void BackupDefaultParameter(MergedCompiledCommandParameter parameter) { }

        public void RestoreDefaultParameterValues(IEnumerable<MergedCompiledCommandParameter> parameters) { }

        public CommandParameterInternal RentPipelineCpi() => null;

        public void ReturnPipelineCpi(CommandParameterInternal cpi) { }

        public bool ApplyDefaultParameterBinding(string caller, bool isDynamic, uint currentParameterSetFlag) => false;
    }

    internal static class ParameterSetResolverTestFactory
    {
        internal static MergedCommandParameterMetadata BuildMetadata(params RuntimeDefinedParameter[] parameters)
        {
            var dict = new RuntimeDefinedParameterDictionary();
            foreach (RuntimeDefinedParameter parameter in parameters)
            {
                dict.Add(parameter.Name, parameter);
            }

            var internalMetadata = InternalParameterMetadata.Get(dict, false, false);
            var mergedMetadata = new MergedCommandParameterMetadata();
            mergedMetadata.AddMetadataForBinder(internalMetadata, ParameterBinderAssociation.DeclaredFormalParameters);
            mergedMetadata.GenerateParameterSetMappingFromMetadata(null);
            return mergedMetadata;
        }

        internal static RuntimeDefinedParameter MakeParam(
            string name,
            Type type = null,
            string setName = null,
            int position = int.MinValue,
            bool mandatory = false,
            bool valueFromPipeline = false,
            bool valueFromPipelineByPropertyName = false)
        {
            type ??= typeof(string);
            var parameterAttribute = new ParameterAttribute();
            if (setName is not null)
            {
                parameterAttribute.ParameterSetName = setName;
            }

            if (position != int.MinValue)
            {
                parameterAttribute.Position = position;
            }

            parameterAttribute.Mandatory = mandatory;
            parameterAttribute.ValueFromPipeline = valueFromPipeline;
            parameterAttribute.ValueFromPipelineByPropertyName = valueFromPipelineByPropertyName;

            return new RuntimeDefinedParameter(name, type, new Collection<Attribute> { parameterAttribute });
        }

        internal static (ParameterSetResolver Resolver, TestBindingContext Context) CreateResolver(
            MergedCommandParameterMetadata metadata = null,
            uint defaultParameterSetFlag = 0,
            List<MergedCompiledCommandParameter> unboundParameters = null,
            Dictionary<string, MergedCompiledCommandParameter> boundParameters = null)
        {
            metadata ??= BuildMetadata();
            var commandMetadata = new CommandMetadata(typeof(PSCmdlet));
            commandMetadata.DefaultParameterSetFlag = defaultParameterSetFlag;

            var context = new TestBindingContext
            {
                UnboundParameters = unboundParameters ?? new List<MergedCompiledCommandParameter>(),
                BoundParameters = boundParameters ?? new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase),
            };

            var resolver = new ParameterSetResolver(commandMetadata, metadata, stateContext: context, opsContext: context);
            return (resolver, context);
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverStateTests
    {
        [Fact]
        public static void Constructor_SetsDefaults()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            Assert.Equal(uint.MaxValue, resolver.CurrentParameterSetFlag);
            Assert.Equal(uint.MaxValue, resolver.PrePipelineProcessingParameterSetFlags);
            Assert.Equal(0u, resolver.ParameterSetToBePrioritizedInPipelineBinding);
        }

        [Fact]
        public static void HasSingleParameterSetSelected_AllSet_ReturnsFalse()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = uint.MaxValue;

            Assert.False(resolver.HasSingleParameterSetSelected);
        }

        [Fact]
        public static void HasSingleParameterSetSelected_Zero_ReturnsFalse()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0;

            Assert.False(resolver.HasSingleParameterSetSelected);
        }

        [Fact]
        public static void HasSingleParameterSetSelected_SingleBit_ReturnsTrue()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x04;

            Assert.True(resolver.HasSingleParameterSetSelected);
        }

        [Fact]
        public static void HasSingleParameterSetSelected_MultipleBits_ReturnsFalse()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x03;

            Assert.False(resolver.HasSingleParameterSetSelected);
        }

        [Fact]
        public static void NarrowByParameterSetFlags_NonZero_NarrowsFlag()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x0F;
            resolver.NarrowByParameterSetFlags(0x03);

            Assert.Equal(0x03u, resolver.CurrentParameterSetFlag);
        }

        [Fact]
        public static void NarrowByParameterSetFlags_Zero_SetsToZero()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x0F;
            resolver.NarrowByParameterSetFlags(0);

            Assert.Equal(0u, resolver.CurrentParameterSetFlag);
        }

        [Fact]
        public static void NarrowByParameterSetFlags_DisjointBits_SetsToZero()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x0C;
            resolver.NarrowByParameterSetFlags(0x03);

            Assert.Equal(0u, resolver.CurrentParameterSetFlag);
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverValidParameterSetCountTests
    {
        [Theory]
        [InlineData(uint.MaxValue, 1)]
        [InlineData(0u, 0)]
        [InlineData(0x01u, 1)]
        [InlineData(0x03u, 2)]
        [InlineData(0x07u, 3)]
        [InlineData(0x0Fu, 4)]
        [InlineData(0xFFu, 8)]
        [InlineData(0xFFFFFFFEu, 31)]
        public static void ValidParameterSetCount_ReturnsExpected(uint flags, int expected)
        {
            Assert.Equal(expected, ParameterSetResolver.ValidParameterSetCount(flags));
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverVerifyTests
    {
        [Fact]
        public static void VerifyParameterSetSelected_AllSetWithDefault_SelectsDefault()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));

            uint defaultFlag = metadata.BindableParameters["Name"].Parameter.ParameterSetFlags;
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata, defaultFlag);

            resolver.CurrentParameterSetFlag = uint.MaxValue;
            resolver.VerifyParameterSetSelected();

            Assert.Equal(defaultFlag, resolver.CurrentParameterSetFlag);
        }

        [Fact]
        public static void VerifyParameterSetSelected_AllSetWithoutDefault_Throws()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));

            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata, defaultParameterSetFlag: 0);
            resolver.CurrentParameterSetFlag = uint.MaxValue;

            Assert.Throws<ParameterBindingException>(() => resolver.VerifyParameterSetSelected());
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverValidateTests
    {
        [Fact]
        public static void ValidateParameterSets_SingleBit_ReturnsOneAndSetsName()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));

            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);
            uint setAFlag = metadata.BindableParameters["Name"].Parameter.ParameterSetFlags;
            resolver.CurrentParameterSetFlag = setAFlag;

            int count = resolver.ValidateParameterSets(prePipelineInput: false, setDefault: true, static _ => false);

            Assert.Equal(1, count);
            Assert.Equal("SetA", context.LastSetName);
        }

        [Fact]
        public static void ValidateParameterSets_AllSetNoDefault_ReturnsOne()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"));
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata, defaultParameterSetFlag: 0);

            resolver.CurrentParameterSetFlag = uint.MaxValue;
            int count = resolver.ValidateParameterSets(prePipelineInput: false, setDefault: true, static _ => false);

            Assert.Equal(1, count);
        }

        [Fact]
        public static void ValidateParameterSets_AllSetDefaultDefinedAndSetDefault_LatchesToDefault()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));

            uint defaultFlag = metadata.BindableParameters["Name"].Parameter.ParameterSetFlags;
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata, defaultFlag);
            resolver.CurrentParameterSetFlag = uint.MaxValue;

            int count = resolver.ValidateParameterSets(prePipelineInput: false, setDefault: true, static _ => false);

            Assert.Equal(1, count);
            Assert.Equal(defaultFlag, resolver.CurrentParameterSetFlag);
        }

        [Fact]
        public static void ValidateParameterSets_SetDefaultFalse_DoesNotMutateFlag()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));

            uint defaultFlag = metadata.BindableParameters["Name"].Parameter.ParameterSetFlags;
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata, defaultFlag);
            resolver.CurrentParameterSetFlag = uint.MaxValue;

            _ = resolver.ValidateParameterSets(prePipelineInput: false, setDefault: false, static _ => false);

            Assert.Equal(uint.MaxValue, resolver.CurrentParameterSetFlag);
        }

        [Fact]
        public static void ValidateParameterSets_AmbiguousNoPipeline_Throws()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("A", setName: "SetA", mandatory: true),
                ParameterSetResolverTestFactory.MakeParam("B", setName: "SetB", mandatory: true));

            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);
            uint setA = metadata.BindableParameters["A"].Parameter.ParameterSetFlags;
            uint setB = metadata.BindableParameters["B"].Parameter.ParameterSetFlags;
            resolver.CurrentParameterSetFlag = setA | setB;
            context.UnboundParameters = new List<MergedCompiledCommandParameter>
            {
                metadata.BindableParameters["A"],
                metadata.BindableParameters["B"],
            };

            Assert.Throws<ParameterBindingException>(() => resolver.ValidateParameterSets(prePipelineInput: false, setDefault: false, static _ => false));
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverAmbiguityResolutionTests
    {
        [Fact]
        public static void ResolveAmbiguity_Static_ResolvesToSingleSetAndSetsName()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("A", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("B", setName: "SetB", mandatory: true));

            uint setA = metadata.BindableParameters["A"].Parameter.ParameterSetFlags;
            uint setB = metadata.BindableParameters["B"].Parameter.ParameterSetFlags;

            uint currentFlags = setA | setB;
            string selectedName = null;

            int result = ParameterSetResolver.ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
                new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase),
                new List<MergedCompiledCommandParameter>
                {
                    metadata.BindableParameters["A"],
                    metadata.BindableParameters["B"],
                },
                metadata,
                ref currentFlags,
                name => selectedName = name);

            Assert.Equal(1, result);
            Assert.Equal(setA, currentFlags);
            Assert.Equal("SetA", selectedName);
        }

        [Fact]
        public static void ResolveAmbiguity_Static_Unresolved_ReturnsMinusOne()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("A", setName: "SetA", mandatory: true),
                ParameterSetResolverTestFactory.MakeParam("B", setName: "SetB", mandatory: true));

            uint setA = metadata.BindableParameters["A"].Parameter.ParameterSetFlags;
            uint setB = metadata.BindableParameters["B"].Parameter.ParameterSetFlags;
            uint currentFlags = setA | setB;

            int result = ParameterSetResolver.ResolveParameterSetAmbiguityBasedOnMandatoryParameters(
                new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase),
                new List<MergedCompiledCommandParameter>
                {
                    metadata.BindableParameters["A"],
                    metadata.BindableParameters["B"],
                },
                metadata,
                ref currentFlags,
                setParameterSetName: null);

            Assert.Equal(-1, result);
            Assert.Equal(setA | setB, currentFlags);
        }

        [Fact]
        public static void ThrowAmbiguousParameterSetException_ThrowsBindingException()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            Assert.Throws<ParameterBindingException>(() => resolver.ThrowAmbiguousParameterSetException(uint.MaxValue));
            Assert.NotNull(context.LastException);
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverMandatoryAnalysisTests
    {
        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(2u)]
        [InlineData(3u)]
        [InlineData(0x80000000u)]
        [InlineData(uint.MaxValue)]
        public static void HasUnboundMandatoryParameters_MatchesLegacyForSetSpecificMetadata(uint currentSet)
        {
            var mixed = ParameterSetResolverTestFactory.MakeParam("Mixed", setName: "SetA");
            mixed.Attributes.Add(new ParameterAttribute { ParameterSetName = "SetB", Mandatory = true });
            var allSetMandatory = ParameterSetResolverTestFactory.MakeParam("AllSetMandatory", mandatory: true);
            allSetMandatory.Attributes.Add(new ParameterAttribute { ParameterSetName = "SetA" });
            var allSetOptional = ParameterSetResolverTestFactory.MakeParam("AllSetOptional");
            allSetOptional.Attributes.Add(new ParameterAttribute { ParameterSetName = "SetB", Mandatory = true });
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                mixed,
                allSetMandatory,
                allSetOptional,
                ParameterSetResolverTestFactory.MakeParam("Optional", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("ByValue", setName: "SetA", mandatory: true, valueFromPipeline: true),
                ParameterSetResolverTestFactory.MakeParam("ByProperty", setName: "SetB", mandatory: true, valueFromPipelineByPropertyName: true));
            var (resolver, context) = ParameterSetResolverTestFactory.CreateResolver(metadata);
            resolver.CurrentParameterSetFlag = currentSet;

            Assert.False(resolver.HasUnboundMandatoryParameters());
            foreach (var parameter in metadata.BindableParameters.Values)
            {
                context.UnboundParameters.Clear();
                context.UnboundParameters.Add(parameter);
                bool expected = resolver.GetMissingMandatoryParameters(
                    ParameterSetResolver.ValidParameterSetCount(currentSet),
                    isPipelineInputExpected: false).Count > 0;

                Assert.Equal(expected, resolver.HasUnboundMandatoryParameters());
            }

            context.UnboundParameters = new List<MergedCompiledCommandParameter>(metadata.BindableParameters.Values);
            Assert.Equal(
                resolver.GetMissingMandatoryParameters(
                    ParameterSetResolver.ValidParameterSetCount(currentSet),
                    isPipelineInputExpected: false).Count > 0,
                resolver.HasUnboundMandatoryParameters());
        }

        [Fact]
        public static void HasUnboundMandatoryParameters_HandlesHighestParameterSetBit()
        {
            var parameters = new RuntimeDefinedParameter[32];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = ParameterSetResolverTestFactory.MakeParam(
                    $"Parameter{i}", setName: $"Set{i}", mandatory: true);
            }

            var metadata = ParameterSetResolverTestFactory.BuildMetadata(parameters);
            var (resolver, _) = ParameterSetResolverTestFactory.CreateResolver(
                metadata, unboundParameters: new List<MergedCompiledCommandParameter>(metadata.BindableParameters.Values));
            resolver.CurrentParameterSetFlag = 0x80000000;

            Assert.True(resolver.HasUnboundMandatoryParameters());
            Assert.Single(resolver.GetMissingMandatoryParameters(1, isPipelineInputExpected: false));
        }

        [Fact]
        public static void HasUnboundMandatoryParameters_ReadsCurrentStateWithoutMutatingIt()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Required", setName: "SetA", mandatory: true),
                ParameterSetResolverTestFactory.MakeParam("Optional", setName: "SetB"));
            var (resolver, context) = ParameterSetResolverTestFactory.CreateResolver(metadata);
            var required = metadata.BindableParameters["Required"];
            context.UnboundParameters.Add(required);
            resolver.CurrentParameterSetFlag = metadata.BindableParameters["Optional"].Parameter.ParameterSetFlags;
            resolver.PrePipelineProcessingParameterSetFlags = uint.MaxValue;
            resolver.ParameterSetToBePrioritizedInPipelineBinding = resolver.CurrentParameterSetFlag;
            uint prioritizedSet = resolver.ParameterSetToBePrioritizedInPipelineBinding;
            var unboundParameters = context.UnboundParameters;

            Assert.False(resolver.HasUnboundMandatoryParameters());
            resolver.CurrentParameterSetFlag = required.Parameter.ParameterSetFlags;
            Assert.True(resolver.HasUnboundMandatoryParameters());
            Assert.Single(context.UnboundParameters);
            Assert.Same(required, context.UnboundParameters[0]);

            context.UnboundParameters.Clear();
            context.BoundParameters.Add(required.Parameter.Name, required);
            Assert.False(resolver.HasUnboundMandatoryParameters());
            context.BoundParameters.Clear();
            context.UnboundParameters.Add(required);
            Assert.True(resolver.HasUnboundMandatoryParameters());

            Assert.Same(unboundParameters, context.UnboundParameters);
            Assert.Equal(required.Parameter.ParameterSetFlags, resolver.CurrentParameterSetFlag);
            Assert.Equal(uint.MaxValue, resolver.PrePipelineProcessingParameterSetFlags);
            Assert.Equal(prioritizedSet, resolver.ParameterSetToBePrioritizedInPipelineBinding);
            Assert.Empty(context.BoundParameters);
            Assert.Empty(context.BoundArguments);
            Assert.Null(context.LastSetName);
            Assert.Null(context.LastException);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(31, false)]
        [InlineData(31, true)]
        public static void HasUnboundMandatoryParameters_DoesNotAllocate(int irrelevantParameterCount, bool missing)
        {
            var parameters = new RuntimeDefinedParameter[irrelevantParameterCount + 1];
            parameters[0] = ParameterSetResolverTestFactory.MakeParam("Active", setName: "Active", mandatory: missing);
            for (int i = 1; i < parameters.Length; i++)
            {
                parameters[i] = ParameterSetResolverTestFactory.MakeParam(
                    $"Other{i}", setName: "Other", mandatory: true);
            }

            var metadata = ParameterSetResolverTestFactory.BuildMetadata(parameters);
            var (resolver, _) = ParameterSetResolverTestFactory.CreateResolver(
                metadata, unboundParameters: new List<MergedCompiledCommandParameter>(metadata.BindableParameters.Values));
            resolver.CurrentParameterSetFlag = metadata.BindableParameters["Active"].Parameter.ParameterSetFlags;

            const int iterations = 10_000;
            for (int i = 0; i < iterations; i++)
            {
                resolver.HasUnboundMandatoryParameters();
            }

            int missingCount = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                if (resolver.HasUnboundMandatoryParameters())
                {
                    missingCount++;
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(missing ? iterations : 0, missingCount);
        }

        [Fact]
        public static void NewParameterSetPromptingData_MandatoryNonPipeline_AddsNonPipelineBucket()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA", mandatory: true));
            MergedCompiledCommandParameter parameter = metadata.BindableParameters["Name"];
            ParameterSetSpecificMetadata setMetadata = parameter.Parameter.GetParameterSetData(parameter.Parameter.ParameterSetFlags);

            var promptingData = new Dictionary<uint, ParameterSetPromptingData>();
            uint mandatorySets = ParameterSetResolver.NewParameterSetPromptingData(
                promptingData,
                parameter,
                setMetadata,
                defaultParameterSet: 0,
                pipelineInputExpected: false);

            Assert.Equal(parameter.Parameter.ParameterSetFlags, mandatorySets);
            Assert.True(promptingData.ContainsKey(parameter.Parameter.ParameterSetFlags));
            Assert.Single(promptingData[parameter.Parameter.ParameterSetFlags].NonpipelineableMandatoryParameters);
        }

        [Fact]
        public static void NewParameterSetPromptingData_MandatoryPipeline_AddsPipelineBucket()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Input", setName: "SetA", mandatory: true, valueFromPipeline: true));
            MergedCompiledCommandParameter parameter = metadata.BindableParameters["Input"];
            ParameterSetSpecificMetadata setMetadata = parameter.Parameter.GetParameterSetData(parameter.Parameter.ParameterSetFlags);

            var promptingData = new Dictionary<uint, ParameterSetPromptingData>();
            _ = ParameterSetResolver.NewParameterSetPromptingData(
                promptingData,
                parameter,
                setMetadata,
                defaultParameterSet: 0,
                pipelineInputExpected: true);

            Assert.Single(promptingData[parameter.Parameter.ParameterSetFlags].PipelineableMandatoryParameters);
            Assert.Single(promptingData[parameter.Parameter.ParameterSetFlags].PipelineableMandatoryByValueParameters);
        }

        [Fact]
        public static void CollectNonpipelineableMandatoryParameters_CollectsParametersForSelectedSet()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA", mandatory: true));
            MergedCompiledCommandParameter parameter = metadata.BindableParameters["Name"];
            ParameterSetSpecificMetadata setMetadata = parameter.Parameter.GetParameterSetData(parameter.Parameter.ParameterSetFlags);

            var promptingSet = new ParameterSetPromptingData(parameter.Parameter.ParameterSetFlags, isDefaultSet: false);
            promptingSet.NonpipelineableMandatoryParameters[parameter] = setMetadata;
            var promptingData = new Dictionary<uint, ParameterSetPromptingData>
            {
                [parameter.Parameter.ParameterSetFlags] = promptingSet,
            };

            var result = new List<MergedCompiledCommandParameter>();
            ParameterSetResolver.CollectNonpipelineableMandatoryParameters(promptingData, parameter.Parameter.ParameterSetFlags, result);

            Assert.Single(result);
            Assert.Same(parameter, result[0]);
        }

        [Fact]
        public static void GetMissingMandatoryParameters_NoPipeline_ReturnsMandatoryParameter()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA", mandatory: true));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter name = metadata.BindableParameters["Name"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { name };
            resolver.CurrentParameterSetFlag = name.Parameter.ParameterSetFlags;

            List<MergedCompiledCommandParameter> missing = resolver.GetMissingMandatoryParameters(validParameterSetCount: 1, isPipelineInputExpected: false);

            Assert.Single(missing);
            Assert.Same(name, missing[0]);
        }

        [Fact]
        public static void GetMissingMandatoryParameters_PipelineSingleSet_ReturnsNonPipelineMandatoryParameter()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA", mandatory: true));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter name = metadata.BindableParameters["Name"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { name };
            resolver.CurrentParameterSetFlag = name.Parameter.ParameterSetFlags;

            List<MergedCompiledCommandParameter> missing = resolver.GetMissingMandatoryParameters(validParameterSetCount: 1, isPipelineInputExpected: true);

            Assert.Single(missing);
            Assert.Same(name, missing[0]);
        }

        [Fact]
        public static void GetMissingMandatoryParameters_NoMandatoryParameters_ReturnsEmpty()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA", mandatory: false));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter name = metadata.BindableParameters["Name"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { name };
            resolver.CurrentParameterSetFlag = name.Parameter.ParameterSetFlags;

            List<MergedCompiledCommandParameter> missing = resolver.GetMissingMandatoryParameters(validParameterSetCount: 1, isPipelineInputExpected: false);

            Assert.Empty(missing);
        }
    }

    [Trait("Category", "ParameterBinding")]
    public static class ParameterSetResolverPipelineMethodTests
    {
        [Fact]
        public static void AtLeastOneUnboundValidParameterSetTakesPipelineInput_WhenPresent_ReturnsTrue()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Input", setName: "SetA", valueFromPipeline: true));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter input = metadata.BindableParameters["Input"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { input };

            bool result = resolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput(input.Parameter.ParameterSetFlags);

            Assert.True(result);
        }

        [Fact]
        public static void AtLeastOneUnboundValidParameterSetTakesPipelineInput_WhenAbsent_ReturnsFalse()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter name = metadata.BindableParameters["Name"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { name };

            bool result = resolver.AtLeastOneUnboundValidParameterSetTakesPipelineInput(name.Parameter.ParameterSetFlags);

            Assert.False(result);
        }

        [Fact]
        public static void FilterParameterSetsTakingNoPipelineInput_DelayBindSetNarrowsFlags()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("SetAOnly", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("SetBOnly", setName: "SetB"));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter setAOnly = metadata.BindableParameters["SetAOnly"];
            MergedCompiledCommandParameter setBOnly = metadata.BindableParameters["SetBOnly"];

            context.UnboundParameters = new List<MergedCompiledCommandParameter> { setBOnly };
            resolver.CurrentParameterSetFlag = setAOnly.Parameter.ParameterSetFlags | setBOnly.Parameter.ParameterSetFlags;

            uint filtered = resolver.FilterParameterSetsTakingNoPipelineInput(new List<MergedCompiledCommandParameter> { setAOnly });

            Assert.Equal(setAOnly.Parameter.ParameterSetFlags, filtered);
        }

        [Fact]
        public static void FilterParameterSetsTakingNoPipelineInput_AllSetPipelineInput_LeavesFlagsUntouched()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("AnyInput", valueFromPipeline: true));
            (ParameterSetResolver resolver, TestBindingContext context) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            MergedCompiledCommandParameter anyInput = metadata.BindableParameters["AnyInput"];
            context.UnboundParameters = new List<MergedCompiledCommandParameter> { anyInput };
            resolver.CurrentParameterSetFlag = uint.MaxValue;

            uint filtered = resolver.FilterParameterSetsTakingNoPipelineInput(new List<MergedCompiledCommandParameter>());

            Assert.Equal(uint.MaxValue, filtered);
        }

        [Fact]
        public static void CurrentParameterSetName_UsesCurrentFlag()
        {
            var metadata = ParameterSetResolverTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("Name", setName: "SetA"),
                ParameterSetResolverTestFactory.MakeParam("Id", setName: "SetB"));
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver(metadata);

            resolver.CurrentParameterSetFlag = metadata.BindableParameters["Id"].Parameter.ParameterSetFlags;

            Assert.Equal("SetB", resolver.CurrentParameterSetName);
        }
    }

}
