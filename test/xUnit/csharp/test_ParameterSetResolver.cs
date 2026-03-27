// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public InvocationInfo InvocationInfo { get; set; } = new InvocationInfo(null, null);

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
        public static void NarrowByParameterSetFlags_Zero_NoChange()
        {
            (ParameterSetResolver resolver, _) = ParameterSetResolverTestFactory.CreateResolver();

            resolver.CurrentParameterSetFlag = 0x0F;
            resolver.NarrowByParameterSetFlags(0);

            Assert.Equal(0x0Fu, resolver.CurrentParameterSetFlag);
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
}
