// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    // -----------------------------------------------------------------------
    // Unit tests for the PipelineBindingPlan struct and its nested Entry struct.
    // Verify struct field storage and invariants. Behavioural end-to-end tests
    // for caching are in EndToEnd_PipelineCachingTests.
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public class PipelineBindingPlanTests
    {
        [Fact]
        public void Plan_DefaultStruct_HasZeroCount()
        {
            var plan = new PipelineBindingPlan();
            Assert.Equal(0, plan.Count);
        }

        [Fact]
        public void Plan_DefaultStruct_HasByPropertyNameIsFalse()
        {
            var plan = new PipelineBindingPlan();
            Assert.False(plan.HasByPropertyName);
        }

        [Fact]
        public void Plan_DefaultStruct_FirstObjectTypeNameIsNull()
        {
            var plan = new PipelineBindingPlan();
            Assert.Null(plan.FirstObjectTypeName);
        }

        [Fact]
        public void Plan_DefaultStruct_ResolvedParameterSetFlagIsZero()
        {
            var plan = new PipelineBindingPlan();
            Assert.Equal(0u, plan.ResolvedParameterSetFlag);
        }

        [Fact]
        public void Plan_Entry_StoresIsValueFromPipelineAndFlags()
        {
            var entry = new PipelineBindingPlan.Entry
            {
                IsValueFromPipeline = true,
                Flags = ParameterBindingFlags.ShouldCoerceType,
            };

            Assert.True(entry.IsValueFromPipeline);
            Assert.Equal(ParameterBindingFlags.ShouldCoerceType, entry.Flags);
        }

        [Fact]
        public void Plan_Count_CanBeSetToMatchEntries()
        {
            var entries = new PipelineBindingPlan.Entry[2];
            var plan = new PipelineBindingPlan
            {
                Entries = entries,
                Count = 2,
            };

            Assert.Equal(2, plan.Count);
            Assert.Equal(2, plan.Entries.Length);
        }

        [Fact]
        public void Plan_HasByPropertyName_FalseWhenAllVfp()
        {
            var plan = new PipelineBindingPlan
            {
                Entries = new PipelineBindingPlan.Entry[]
                {
                    new PipelineBindingPlan.Entry { IsValueFromPipeline = true, Flags = ParameterBindingFlags.None },
                    new PipelineBindingPlan.Entry { IsValueFromPipeline = true, Flags = ParameterBindingFlags.None },
                },
                Count = 2,
                HasByPropertyName = false,
            };

            Assert.False(plan.HasByPropertyName);
        }

        [Fact]
        public void Plan_HasByPropertyName_TrueWhenByPropNamePresent()
        {
            var plan = new PipelineBindingPlan
            {
                Entries = new PipelineBindingPlan.Entry[]
                {
                    new PipelineBindingPlan.Entry { IsValueFromPipeline = true,  Flags = ParameterBindingFlags.None },
                    new PipelineBindingPlan.Entry { IsValueFromPipeline = false, Flags = ParameterBindingFlags.None },
                },
                Count = 2,
                HasByPropertyName = true,
            };

            Assert.True(plan.HasByPropertyName);
        }

        [Fact]
        public void Plan_FirstObjectTypeName_CanBeSetForByPropName()
        {
            var plan = new PipelineBindingPlan
            {
                HasByPropertyName = true,
                FirstObjectTypeName = "System.Management.Automation.PSCustomObject",
            };

            Assert.True(plan.HasByPropertyName);
            Assert.NotNull(plan.FirstObjectTypeName);
            Assert.Equal("System.Management.Automation.PSCustomObject", plan.FirstObjectTypeName);
        }

        [Fact]
        public void Plan_FirstObjectTypeName_NullForVfpOnlyPlan()
        {
            var plan = new PipelineBindingPlan
            {
                HasByPropertyName = false,
                FirstObjectTypeName = null,
            };

            Assert.False(plan.HasByPropertyName);
            Assert.Null(plan.FirstObjectTypeName);
        }
    }
}
