// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.PowerShell.Commands;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Lightweight stub implementing IParameterBindingContext for resolver tests.
    /// </summary>
    internal sealed class TestBindingContext : IParameterBindingContext
    {
        public ICollection<MergedCompiledCommandParameter> UnboundParameters { get; set; } = new List<MergedCompiledCommandParameter>();

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; set; } = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        public InvocationInfo InvocationInfo { get; set; } = new InvocationInfo(new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);

        public string LastSetName { get; private set; }

        public ParameterBindingException LastException { get; private set; }

        public void SetParameterSetName(string parameterSetName)
        {
            LastSetName = parameterSetName;
        }

        public void ThrowBindingException(ParameterBindingException exception)
        {
            LastException = exception;
            throw exception;
        }
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

            var resolver = new ParameterSetResolver(commandMetadata, metadata, context);
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
