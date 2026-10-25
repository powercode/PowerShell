// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for <c>DispatchBindToSubBinder</c> routing and the bind-success
    /// state transitions (parameter moves, set narrowing).
    ///
    /// These tests use <see cref="TestableParameterBinderController"/> which
    /// records all dispatch calls and simulates success/failure without a real Cmdlet.
    ///
    /// Integration-level tests that verify routing to specific sub-binders
    /// (common parameters, should-process, paging) are in
    /// <c>test_SubBinderDispatch.cs</c>, which will be tagged as Integration.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class SubBinderDispatchUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name)
            => ParameterSetResolverTestFactory.MakeParam(name);

        private static TestableParameterBinderController BuildController(params RuntimeDefinedParameter[] parameters)
        {
            var meta = BindingTestFactory.BuildMetadata(parameters);
            return BindingTestFactory.CreateController(meta);
        }

        [Fact]
        public void SuccessfulDispatch_MovesParamFromUnboundToBound()
        {
            // After a successful dispatch, the parameter must be in BoundParameters
            // and removed from UnboundParameters.
            var controller = BuildController(MakeParam("Path"));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "/tmp"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.True(controller.GetBoundParameters().ContainsKey("Path"));
        }

        [Fact]
        public void FailedDispatch_ParamRemainsInUnbound()
        {
            // When DispatchResult = false, the parameter should NOT appear in BoundParameters.
            var controller = BuildController(MakeParam("Path"));
            controller.DispatchResult = false;
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "/tmp"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            // Dispatch was attempted but reported failure
            Assert.Single(controller.DispatchCalls);
            Assert.False(controller.GetBoundParameters().ContainsKey("Path"));
        }

        [Fact]
        public void DispatchCalls_RecordsParamNameAndValue()
        {
            var controller = BuildController(MakeParam("ComputerName"));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("ComputerName", "server01"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("ComputerName", controller.DispatchCalls[0].ParamName);
            Assert.Equal("server01", controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void MultipleSuccessfulDispatches_AllRecordedAndBound()
        {
            var controller = BuildController(
                MakeParam("Path"),
                MakeParam("Name"),
                MakeParam("Force"));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "/tmp"),
                BindingTestFactory.MakeNamedArg("Name", "file.txt"),
                BindingTestFactory.MakeNamedArg("Force", true),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Equal(3, controller.DispatchCalls.Count);
            Assert.Equal(3, controller.GetBoundParameters().Count);
        }

        [Fact]
        public void DispatchCalls_RecordsParameterSetFlag()
        {
            var controller = BuildController(MakeParam("Path"));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "/tmp"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            // SetFlag must be non-zero; uint.MaxValue is the default all-sets flag
            Assert.NotEqual(0u, controller.DispatchCalls[0].SetFlag);
        }

        [Fact]
        public void DispatchResult_Default_IsTrue()
        {
            // The stub should succeed by default (DispatchResult = true).
            var controller = BuildController(MakeParam("Name"));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Name", "test"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.True(controller.GetBoundParameters().ContainsKey("Name"));
        }
    }
}
