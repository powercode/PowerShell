// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    // ---------------------------------------------------------------------------
    // Helper factory used across all positional-binding test classes
    // ---------------------------------------------------------------------------
    internal static class PositionalTestFactory
    {
        /// <summary>
        /// Create a MergedCompiledCommandParameter with a single parameter set entry
        /// at the specified position and with the specified parameter-set flag.
        /// </summary>
        internal static MergedCompiledCommandParameter CreateParameter(
            string name,
            int position,
            uint paramSetFlag,
            string paramSetName = "Set1",
            bool valueFromRemainingArguments = false)
        {
            var attr = new ParameterAttribute
            {
                Position = position,
                ParameterSetName = paramSetName,
                ValueFromRemainingArguments = valueFromRemainingArguments
            };
            var rdp = new RuntimeDefinedParameter(name, typeof(string),
                new Collection<Attribute> { attr });
            var ccp = new CompiledCommandParameter(rdp, false);

            // Wire up the flag so EvaluateUnboundPositionalParameters can match.
            ccp.ParameterSetFlags = paramSetFlag;
            ccp.ParameterSetData[paramSetName].ParameterSetFlag = paramSetFlag;

            return new MergedCompiledCommandParameter(ccp, ParameterBinderAssociation.DeclaredFormalParameters);
        }

        /// <summary>
        /// Create a non-positional parameter (Position == int.MinValue).
        /// </summary>
        internal static MergedCompiledCommandParameter CreateNonPositionalParameter(
            string name,
            uint paramSetFlag,
            string paramSetName = "Set1")
        {
            // Position defaults to int.MinValue when not set.
            var attr = new ParameterAttribute { ParameterSetName = paramSetName };
            var rdp = new RuntimeDefinedParameter(name, typeof(string),
                new Collection<Attribute> { attr });
            var ccp = new CompiledCommandParameter(rdp, false);
            ccp.ParameterSetFlags = paramSetFlag;
            ccp.ParameterSetData[paramSetName].ParameterSetFlag = paramSetFlag;
            return new MergedCompiledCommandParameter(ccp, ParameterBinderAssociation.DeclaredFormalParameters);
        }

        /// <summary>
        /// Create a PositionalCommandParameter containing a single ParameterSetSpecificMetadata entry.
        /// </summary>
        internal static PositionalCommandParameter CreatePositionalCommandParameter(
            MergedCompiledCommandParameter mergedParam,
            uint paramSetFlag,
            int position)
        {
            var pcp = new PositionalCommandParameter(mergedParam);
            var setData = new ParameterSetSpecificMetadata(
                isMandatory: false,
                position: position,
                valueFromRemainingArguments: false,
                valueFromPipeline: false,
                valueFromPipelineByPropertyName: false,
                helpMessageBaseName: null,
                helpMessageResourceId: null,
                helpMessage: null)
            {
                ParameterSetFlag = paramSetFlag
            };
            pcp.ParameterSetData.Add(setData);
            return pcp;
        }
    }

    // ---------------------------------------------------------------------------
    // ContainsPositionalParameterInSet tests
    // ---------------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class ContainsPositionalParameterInSetTests
    {
        [Fact]
        public static void EmptyDictionary_ReturnsFalse()
        {
            var dict = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>();
            var param = PositionalTestFactory.CreateParameter("A", 0, 0x01u);
            bool result = ParameterBinderController.ContainsPositionalParameterInSet(dict, param, 0x01u);
            Assert.False(result);
        }

        [Fact]
        public static void DictionaryContainsOnlySameParam_ReturnsFalse()
        {
            var param = PositionalTestFactory.CreateParameter("A", 0, 0x01u);
            var pcp = PositionalTestFactory.CreatePositionalCommandParameter(param, 0x01u, 0);
            var dict = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>
            {
                { param, pcp }
            };

            // Same parameter reference is always skipped — no conflict.
            bool result = ParameterBinderController.ContainsPositionalParameterInSet(dict, param, 0x01u);
            Assert.False(result);
        }

        [Fact]
        public static void DifferentParam_SameSet_ReturnsTrue()
        {
            const uint setFlag = 0x01u;
            var paramA = PositionalTestFactory.CreateParameter("A", 0, setFlag);
            var paramB = PositionalTestFactory.CreateParameter("B", 0, setFlag);
            var pcpA = PositionalTestFactory.CreatePositionalCommandParameter(paramA, setFlag, 0);

            var dict = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>
            {
                { paramA, pcpA }
            };

            bool result = ParameterBinderController.ContainsPositionalParameterInSet(dict, paramB, setFlag);
            Assert.True(result);
        }

        [Fact]
        public static void DifferentParam_DifferentSet_ReturnsFalse()
        {
            var paramA = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, 0x02u, "Set2");
            var pcpA = PositionalTestFactory.CreatePositionalCommandParameter(paramA, 0x01u, 0);

            var dict = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>
            {
                { paramA, pcpA }
            };

            // paramB is in set 0x02 — paramA's entry is only in set 0x01, no overlap.
            bool result = ParameterBinderController.ContainsPositionalParameterInSet(dict, paramB, 0x02u);
            Assert.False(result);
        }

        [Fact]
        public static void MultipleParams_OnlyOneInTargetSet_ReturnsTrue()
        {
            const uint setFlag1 = 0x01u;
            const uint setFlag2 = 0x02u;
            var paramA = PositionalTestFactory.CreateParameter("A", 0, setFlag1, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, setFlag2, "Set2");
            var paramC = PositionalTestFactory.CreateParameter("C", 0, setFlag1, "Set1");

            var pcpA = PositionalTestFactory.CreatePositionalCommandParameter(paramA, setFlag1, 0);
            var pcpB = PositionalTestFactory.CreatePositionalCommandParameter(paramB, setFlag2, 0);

            var dict = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>
            {
                { paramA, pcpA },
                { paramB, pcpB }
            };

            // paramC is in set 0x01, paramA is also in set 0x01 → conflict detected.
            bool result = ParameterBinderController.ContainsPositionalParameterInSet(dict, paramC, setFlag1);
            Assert.True(result);
        }
    }

    // ---------------------------------------------------------------------------
    // AddNewPosition tests
    // ---------------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class AddNewPositionTests
    {
        private static ParameterSetSpecificMetadata MakeSetData(uint setFlag, int position)
        {
            var data = new ParameterSetSpecificMetadata(
                isMandatory: false,
                position: position,
                valueFromRemainingArguments: false,
                valueFromPipeline: false,
                valueFromPipelineByPropertyName: false,
                helpMessageBaseName: null,
                helpMessageResourceId: null,
                helpMessage: null);
            data.ParameterSetFlag = setFlag;
            return data;
        }

        [Fact]
        public static void NewPosition_CreatesEntry()
        {
            var result =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();
            var param = PositionalTestFactory.CreateParameter("A", 0, 0x01u);
            var setData = MakeSetData(0x01u, 0);

            ParameterBinderController.AddNewPosition(result, 0, param, setData);

            Assert.Single(result);
            Assert.True(result.ContainsKey(0));
            Assert.True(result[0].ContainsKey(param));
        }

        [Fact]
        public static void SamePosition_DifferentSets_NoDuplicateEntry()
        {
            // Two different parameters at position 0 in different parameter sets.
            // This is a valid scenario (multi-set commands).
            var result =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();

            var paramA = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, 0x02u, "Set2");
            var setDataA = MakeSetData(0x01u, 0);
            var setDataB = MakeSetData(0x02u, 0);

            ParameterBinderController.AddNewPosition(result, 0, paramA, setDataA);
            // paramB is in a different set — should NOT conflict.
            ParameterBinderController.AddNewPosition(result, 0, paramB, setDataB);

            // Position 0 now contains both paramA and paramB.
            Assert.Single(result);
            Assert.Equal(2, result[0].Count);
        }

        [Fact]
        public static void SamePosition_SameSet_ThrowsInvalidOperation()
        {
            // Two different parameters at position 0 in the SAME parameter set — ambiguous.
            var result =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();

            var paramA = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, 0x01u, "Set1");
            var setDataA = MakeSetData(0x01u, 0);
            var setDataB = MakeSetData(0x01u, 0);

            ParameterBinderController.AddNewPosition(result, 0, paramA, setDataA);
            // AddNewPosition throws PSInvalidOperationException (a subclass of InvalidOperationException)
            // when two different parameters share the same position in the same parameter set.
            Assert.ThrowsAny<InvalidOperationException>(() =>
                ParameterBinderController.AddNewPosition(result, 0, paramB, setDataB));
        }

        [Fact]
        public static void SameParam_TwoSets_AddsSetDataToExistingEntry()
        {
            // A parameter that appears in two parameter sets at the same position.
            // Both set-data entries should be added to the same PositionalCommandParameter.
            var result =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();

            // Build a parameter that belongs to two sets
            var attrSet1 = new ParameterAttribute { Position = 0, ParameterSetName = "Set1" };
            var attrSet2 = new ParameterAttribute { Position = 0, ParameterSetName = "Set2" };
            var rdp = new RuntimeDefinedParameter("A", typeof(string),
                new Collection<Attribute> { attrSet1, attrSet2 });
            var ccp = new CompiledCommandParameter(rdp, false);
            ccp.ParameterSetFlags = 0x03u;
            ccp.ParameterSetData["Set1"].ParameterSetFlag = 0x01u;
            ccp.ParameterSetData["Set2"].ParameterSetFlag = 0x02u;
            var param = new MergedCompiledCommandParameter(ccp, ParameterBinderAssociation.DeclaredFormalParameters);

            var setData1 = MakeSetData(0x01u, 0);
            var setData2 = MakeSetData(0x02u, 0);

            ParameterBinderController.AddNewPosition(result, 0, param, setData1);
            ParameterBinderController.AddNewPosition(result, 0, param, setData2);

            // Should be only 1 PositionalCommandParameter entry for this param,
            // but with 2 set-data entries.
            Assert.Single(result[0]);
            Assert.Equal(2, result[0][param].ParameterSetData.Count);
        }

        [Fact]
        public static void DifferentPositions_CreateSeparateEntries()
        {
            var result =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();

            var paramA = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 1, 0x01u, "Set1");

            ParameterBinderController.AddNewPosition(result, 0, paramA, MakeSetData(0x01u, 0));
            ParameterBinderController.AddNewPosition(result, 1, paramB, MakeSetData(0x01u, 1));

            Assert.Equal(2, result.Count);
            Assert.True(result.ContainsKey(0));
            Assert.True(result.ContainsKey(1));
        }
    }

    // ---------------------------------------------------------------------------
    // EvaluateUnboundPositionalParameters tests
    // ---------------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class EvaluateUnboundPositionalParametersTests
    {
        [Fact]
        public static void EmptyList_ReturnsEmptyDictionary()
        {
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter>(), 0x01u);
            Assert.Empty(result);
        }

        [Fact]
        public static void SinglePositionalParam_InSet_AddedAtCorrectPosition()
        {
            var param = PositionalTestFactory.CreateParameter("A", 2, 0x01u, "Set1");
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { param }, 0x01u);

            Assert.Single(result);
            Assert.True(result.ContainsKey(2));
            Assert.True(result[2].ContainsKey(param));
        }

        [Fact]
        public static void ParameterNotInRequestedSet_NotIncluded()
        {
            // Param is in set 0x02, but we ask for set 0x01.
            var param = PositionalTestFactory.CreateParameter("A", 0, 0x02u, "Set2");
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { param }, 0x01u);

            Assert.Empty(result);
        }

        [Fact]
        public static void NonPositionalParam_NotIncluded()
        {
            var param = PositionalTestFactory.CreateNonPositionalParameter("A", 0x01u, "Set1");
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { param }, 0x01u);

            Assert.Empty(result);
        }

        [Fact]
        public static void VRAParam_NotIncluded()
        {
            var param = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1",
                valueFromRemainingArguments: true);
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { param }, 0x01u);

            Assert.Empty(result);
        }

        [Fact]
        public static void MultipleParams_DifferentPositions_AllIncluded()
        {
            var paramA = PositionalTestFactory.CreateParameter("A", 0, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 1, 0x01u, "Set1");
            var paramC = PositionalTestFactory.CreateParameter("C", 2, 0x01u, "Set1");

            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { paramA, paramB, paramC }, 0x01u);

            Assert.Equal(3, result.Count);
            Assert.True(result.ContainsKey(0));
            Assert.True(result.ContainsKey(1));
            Assert.True(result.ContainsKey(2));
        }

        [Fact]
        public static void DictIsSorted_PositionsInAscendingOrder()
        {
            // Add in reverse order; sorted dict should give positions 0, 1, 2.
            var paramA = PositionalTestFactory.CreateParameter("A", 2, 0x01u, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, 0x01u, "Set1");
            var paramC = PositionalTestFactory.CreateParameter("C", 1, 0x01u, "Set1");

            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { paramA, paramB, paramC }, 0x01u);

            int prev = -1;
            foreach (int key in result.Keys)
            {
                Assert.True(key > prev);
                prev = key;
            }
        }

        [Fact]
        public static void IsInAllSets_Param_AlwaysIncluded()
        {
            // Param is in "all" sets, so it should appear even with any validParamSetFlag.
            var attr = new ParameterAttribute
            {
                Position = 0,
                ParameterSetName = ParameterAttribute.AllParameterSets
            };
            var rdp = new RuntimeDefinedParameter("A", typeof(string),
                new Collection<Attribute> { attr });
            var ccp = new CompiledCommandParameter(rdp, false);
            ccp.ParameterSetFlags = 0x01u;
            ccp.IsInAllSets = true;
            // Mark special all-sets flag on the set data entry.
            ccp.ParameterSetData[ParameterAttribute.AllParameterSets].IsInAllSets = true;
            ccp.ParameterSetData[ParameterAttribute.AllParameterSets].ParameterSetFlag = 0x01u;
            var param = new MergedCompiledCommandParameter(ccp, ParameterBinderAssociation.DeclaredFormalParameters);

            // Ask for set 0x04 — despite the param only having flag 0x01, IsInAllSets → include it.
            var result = ParameterBinderController.EvaluateUnboundPositionalParameters(
                new List<MergedCompiledCommandParameter> { param }, 0x04u);

            Assert.Single(result);
        }
    }

    // ---------------------------------------------------------------------------
    // UpdatePositionalDictionary tests
    // ---------------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class UpdatePositionalDictionaryTests
    {
        private static SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>
            BuildDict(params (MergedCompiledCommandParameter param, PositionalCommandParameter pcp, int pos)[] entries)
        {
            var dict =
                new SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>();
            foreach (var (param, pcp, pos) in entries)
            {
                if (!dict.TryGetValue(pos, out var inner))
                {
                    inner = new Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>();
                    dict[pos] = inner;
                }
                inner[param] = pcp;
            }
            return dict;
        }

        [Fact]
        public static void AllInValidSet_NothingRemoved()
        {
            const uint setFlag = 0x01u;
            var param = PositionalTestFactory.CreateParameter("A", 0, setFlag);
            var pcp = PositionalTestFactory.CreatePositionalCommandParameter(param, setFlag, 0);
            var dict = BuildDict((param, pcp, 0));

            ParameterBinderController.UpdatePositionalDictionary(dict, setFlag);

            Assert.Single(dict);
            Assert.Single(dict[0]);
        }

        [Fact]
        public static void ParamNotInValidSet_SetDataEntryRemoved()
        {
            const uint setFlagA = 0x01u;
            const uint setFlagB = 0x02u;
            var param = PositionalTestFactory.CreateParameter("A", 0, setFlagA, "Set1");
            var pcp = PositionalTestFactory.CreatePositionalCommandParameter(param, setFlagA, 0);
            var dict = BuildDict((param, pcp, 0));

            // Filter to set 0x02 — the only set-data (0x01) is removed.
            ParameterBinderController.UpdatePositionalDictionary(dict, setFlagB);

            // The parameter's set-data list should be empty → parameter removed from position.
            Assert.Empty(dict[0]);
        }

        [Fact]
        public static void MixedSets_OnlyMatchingSetDataKept()
        {
            const uint setFlagA = 0x01u;
            const uint setFlagB = 0x02u;

            var paramA = PositionalTestFactory.CreateParameter("A", 0, setFlagA, "Set1");
            var paramB = PositionalTestFactory.CreateParameter("B", 0, setFlagB, "Set2");

            var pcpA = PositionalTestFactory.CreatePositionalCommandParameter(paramA, setFlagA, 0);
            var pcpB = PositionalTestFactory.CreatePositionalCommandParameter(paramB, setFlagB, 0);

            var dict = BuildDict((paramA, pcpA, 0), (paramB, pcpB, 0));

            // Restrict to Set2 (0x02) — paramA's set-data is removed; paramB stays.
            ParameterBinderController.UpdatePositionalDictionary(dict, setFlagB);

            Assert.Single(dict[0]); // only paramB remains
            Assert.True(dict[0].ContainsKey(paramB));
        }
    }
}
