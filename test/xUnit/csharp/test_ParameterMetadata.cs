// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PSTests.Parallel
{
    // -----------------------------------------------------------------------
    // ParameterCollectionTypeInformation tests
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class ParameterCollectionTypeInformationTests
    {
        [Fact]
        public static void Array_Type_Detected()
        {
            var info = new ParameterCollectionTypeInformation(typeof(string[]));
            Assert.Equal(ParameterCollectionType.Array, info.ParameterCollectionType);
            Assert.Equal(typeof(string), info.ElementType);
        }

        [Fact]
        public static void IList_Type_Detected()
        {
            var info = new ParameterCollectionTypeInformation(typeof(ArrayList));
            Assert.Equal(ParameterCollectionType.IList, info.ParameterCollectionType);
        }

        [Fact]
        public static void ICollectionGeneric_Type_Detected()
        {
            var info = new ParameterCollectionTypeInformation(typeof(HashSet<int>));
            Assert.Equal(ParameterCollectionType.ICollectionGeneric, info.ParameterCollectionType);
            Assert.Equal(typeof(int), info.ElementType);
        }

        [Fact]
        public static void Collection_Of_T_Uses_IList()
        {
            var info = new ParameterCollectionTypeInformation(typeof(Collection<string>));
            Assert.Equal(ParameterCollectionType.IList, info.ParameterCollectionType);
            Assert.Equal(typeof(string), info.ElementType);
        }

        [Fact]
        public static void NonCollection_Type_Detected()
        {
            var info = new ParameterCollectionTypeInformation(typeof(string));
            Assert.Equal(ParameterCollectionType.NotCollection, info.ParameterCollectionType);
        }

        [Fact]
        public static void IDictionary_Not_Treated_As_Collection()
        {
            var info = new ParameterCollectionTypeInformation(typeof(Hashtable));
            Assert.Equal(ParameterCollectionType.NotCollection, info.ParameterCollectionType);
        }

        [Fact]
        public static void GenericIDictionary_Not_Treated_As_Collection()
        {
            var info = new ParameterCollectionTypeInformation(typeof(Dictionary<string, object>));
            Assert.Equal(ParameterCollectionType.NotCollection, info.ParameterCollectionType);
        }
    }

    // -----------------------------------------------------------------------
    // CompiledCommandParameter tests (constructed via RuntimeDefinedParameter)
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class CompiledCommandParameterTests
    {
        [Fact]
        public static void Name_And_Type_Set_Correctly()
        {
            var rdp = new RuntimeDefinedParameter("MyParam", typeof(string), new Collection<Attribute>());
            var param = new CompiledCommandParameter(rdp, false);

            Assert.Equal("MyParam", param.Name);
            Assert.Equal(typeof(string), param.Type);
        }

        [Fact]
        public static void ValidationAttributes_Collected()
        {
            var attrs = new Collection<Attribute>
            {
                new ParameterAttribute(),
                new ValidateNotNullAttribute()
            };
            var rdp = new RuntimeDefinedParameter("MyParam", typeof(string), attrs);
            var param = new CompiledCommandParameter(rdp, false);

            Assert.True(param.CannotBeNull, "CannotBeNull should be set when ValidateNotNull is present");
        }

        [Fact]
        public static void Aliases_Collected()
        {
            var attrs = new Collection<Attribute>
            {
                new ParameterAttribute(),
                new AliasAttribute("CN", "Computer")
            };
            var rdp = new RuntimeDefinedParameter("ComputerName", typeof(string), attrs);
            var param = new CompiledCommandParameter(rdp, false);

            Assert.Contains("CN", param.Aliases);
            Assert.Contains("Computer", param.Aliases);
        }

        [Fact]
        public static void ParameterSetData_Collected()
        {
            var paramAttr = new ParameterAttribute { ParameterSetName = "SetA" };
            var attrs = new Collection<Attribute> { paramAttr };
            var rdp = new RuntimeDefinedParameter("MyParam", typeof(string), attrs);
            var param = new CompiledCommandParameter(rdp, false);

            Assert.True(param.ParameterSetData.ContainsKey("SetA"));
        }

        [Fact]
        public static void PSTypeName_Collected()
        {
            var attrs = new Collection<Attribute>
            {
                new ParameterAttribute(),
                new PSTypeNameAttribute("MyNamespace.MyType")
            };
            var rdp = new RuntimeDefinedParameter("MyParam", typeof(PSObject), attrs);
            var param = new CompiledCommandParameter(rdp, false);

            Assert.Equal("MyNamespace.MyType", param.PSTypeName);
        }
    }

    // -----------------------------------------------------------------------
    // MergedCommandParameterMetadata tests
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class MergedCommandParameterMetadataTests
    {
        /// <summary>
        /// Helper: build a MergedCommandParameterMetadata from a list of RuntimeDefinedParameters.
        /// </summary>
        private static MergedCommandParameterMetadata BuildMetadata(params RuntimeDefinedParameter[] parameters)
        {
            var dict = new RuntimeDefinedParameterDictionary();
            foreach (var p in parameters)
            {
                dict.Add(p.Name, p);
            }

            var internalMeta = InternalParameterMetadata.Get(dict, false, false);
            var merged = new MergedCommandParameterMetadata();
            merged.AddMetadataForBinder(internalMeta, ParameterBinderAssociation.DeclaredFormalParameters);
            merged.GenerateParameterSetMappingFromMetadata(null);
            return merged;
        }

        private static RuntimeDefinedParameter MakeParam(string name, Type type = null, string setName = null, int position = int.MinValue, string[] aliases = null)
        {
            type ??= typeof(string);
            var attrs = new Collection<Attribute>();
            var paramAttr = new ParameterAttribute();
            if (setName != null) paramAttr.ParameterSetName = setName;
            if (position != int.MinValue) paramAttr.Position = position;
            attrs.Add(paramAttr);
            if (aliases != null) attrs.Add(new AliasAttribute(aliases));
            return new RuntimeDefinedParameter(name, type, attrs);
        }

        [Fact]
        public static void ExactMatch_Returns_Parameter()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            var result = meta.GetMatchingParameter("Path", false, true, null);
            Assert.NotNull(result);
            Assert.Equal("Path", result.Parameter.Name);
        }

        [Fact]
        public static void PrefixMatch_Returns_Parameter()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            var result = meta.GetMatchingParameter("Pa", false, true, null);
            Assert.NotNull(result);
            Assert.Equal("Path", result.Parameter.Name);
        }

        [Fact]
        public static void AmbiguousPrefix_Throws()
        {
            var meta = BuildMetadata(MakeParam("Path"), MakeParam("Process"));
            Assert.Throws<ParameterBindingException>(() =>
                meta.GetMatchingParameter("P", false, true, null));
        }

        [Fact]
        public static void AliasMatch_Returns_Parameter()
        {
            var meta = BuildMetadata(MakeParam("ComputerName", aliases: new[] { "CN" }));
            var result = meta.GetMatchingParameter("CN", false, true, null);
            Assert.NotNull(result);
            Assert.Equal("ComputerName", result.Parameter.Name);
        }

        [Fact]
        public static void NotFound_Returns_Null()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            var result = meta.GetMatchingParameter("NonExistent", false, true, null);
            Assert.Null(result);
        }

        [Fact]
        public static void NotFound_Throws()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            Assert.Throws<ParameterBindingException>(() =>
                meta.GetMatchingParameter("NonExistent", true, true, null));
        }

        [Fact]
        public static void CaseInsensitive_Match()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            var result = meta.GetMatchingParameter("path", false, true, null);
            Assert.NotNull(result);
            Assert.Equal("Path", result.Parameter.Name);
        }

        [Fact]
        public static void LeadingDash_Stripped()
        {
            var meta = BuildMetadata(MakeParam("Path"));
            var result = meta.GetMatchingParameter("-Path", false, true, null);
            Assert.NotNull(result);
            Assert.Equal("Path", result.Parameter.Name);
        }

        [Fact]
        public static void DuplicateName_Throws_MetadataException()
        {
            var dict = new RuntimeDefinedParameterDictionary();
            var attrs = new Collection<Attribute> { new ParameterAttribute() };
            dict.Add("Path", new RuntimeDefinedParameter("Path", typeof(string), attrs));

            var internalMeta = InternalParameterMetadata.Get(dict, false, false);
            var merged = new MergedCommandParameterMetadata();
            merged.AddMetadataForBinder(internalMeta, ParameterBinderAssociation.DeclaredFormalParameters);

            // Adding the same metadata again should throw MetadataException for duplicate name
            Assert.Throws<MetadataException>(() =>
                merged.AddMetadataForBinder(internalMeta, ParameterBinderAssociation.DeclaredFormalParameters));
        }

        [Fact]
        public static void AliasConflictsWithParameterName_Throws_MetadataException()
        {
            // Define "Path" with alias "Process", then add "Process" — alias conflicts with param name
            var dict1 = new RuntimeDefinedParameterDictionary();
            var attrs1 = new Collection<Attribute> { new ParameterAttribute(), new AliasAttribute("Process") };
            dict1.Add("Path", new RuntimeDefinedParameter("Path", typeof(string), attrs1));

            var dict2 = new RuntimeDefinedParameterDictionary();
            var attrs2 = new Collection<Attribute> { new ParameterAttribute() };
            dict2.Add("Process", new RuntimeDefinedParameter("Process", typeof(string), attrs2));

            var meta1 = InternalParameterMetadata.Get(dict1, false, false);
            var meta2 = InternalParameterMetadata.Get(dict2, false, false);

            var merged = new MergedCommandParameterMetadata();
            merged.AddMetadataForBinder(meta1, ParameterBinderAssociation.DeclaredFormalParameters);

            Assert.Throws<MetadataException>(() =>
                merged.AddMetadataForBinder(meta2, ParameterBinderAssociation.DeclaredFormalParameters));
        }
    }
}
