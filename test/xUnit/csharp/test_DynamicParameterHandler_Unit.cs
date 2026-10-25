// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for dynamic-parameter metadata merge behavior.
    /// These tests validate the merge/conflict logic used by DynamicParameterHandler.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class DynamicParameterHandlerUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name, string alias = null)
        {
            var attrs = new Collection<Attribute>
            {
                new ParameterAttribute { ParameterSetName = ParameterAttribute.AllParameterSets },
            };

            if (!string.IsNullOrEmpty(alias))
            {
                attrs.Add(new AliasAttribute(alias));
            }

            return new RuntimeDefinedParameter(name, typeof(string), attrs);
        }

        private static InternalParameterMetadata BuildDynamicMetadata(params RuntimeDefinedParameter[] dynamicParameters)
        {
            var dict = new RuntimeDefinedParameterDictionary();
            foreach (var p in dynamicParameters)
            {
                dict.Add(p.Name, p);
            }

            return InternalParameterMetadata.Get(dict, processingDynamicParameters: true, checkNames: true);
        }

        [Fact]
        public void DiscoverAndMerge_AddsDynamicParamsToMetadata()
        {
            var staticMetadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var dynamicMetadata = BuildDynamicMetadata(MakeParam("DynamicParam"));

            staticMetadata.AddMetadataForBinder(dynamicMetadata, ParameterBinderAssociation.DynamicParameters);

            Assert.True(staticMetadata.BindableParameters.ContainsKey("Path"));
            Assert.True(staticMetadata.BindableParameters.ContainsKey("DynamicParam"));
        }

        [Fact]
        public void DiscoverAndMerge_NameConflict_ThrowsMetadataException()
        {
            var staticMetadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var dynamicMetadata = BuildDynamicMetadata(MakeParam("Path"));

            Assert.Throws<MetadataException>(() =>
                staticMetadata.AddMetadataForBinder(dynamicMetadata, ParameterBinderAssociation.DynamicParameters));
        }

        [Fact]
        public void DiscoverAndMerge_AliasConflict_ThrowsMetadataException()
        {
            var staticMetadata = BindingTestFactory.BuildMetadata(MakeParam("Path", alias: "FullPath"));
            var dynamicMetadata = BuildDynamicMetadata(MakeParam("DynamicParam", alias: "FullPath"));

            Assert.Throws<MetadataException>(() =>
                staticMetadata.AddMetadataForBinder(dynamicMetadata, ParameterBinderAssociation.DynamicParameters));
        }
    }
}
