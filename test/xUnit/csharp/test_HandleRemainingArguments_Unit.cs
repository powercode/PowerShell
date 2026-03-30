// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for the overflow and remaining-arguments phase of parameter binding,
    /// exercising the algorithm behaviors that precede and set up <c>HandleRemainingArguments</c>.
    /// Tests use <see cref="TestableParameterBinderController"/> without <c>PowerShell.Create()</c>.
    ///
    /// Integration-level tests for VRA capture and <c>VerifyArgumentsProcessed</c>
    /// are in <c>test_RemainingArguments.cs</c>, which will be tagged as Integration.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class HandleRemainingArgumentsUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name, int position = int.MinValue)
            => ParameterSetResolverTestFactory.MakeParam(name, position: position);

        private static RuntimeDefinedParameter MakeVRAParam(string name)
        {
            var attr = new System.Management.Automation.ParameterAttribute
            {
                ValueFromRemainingArguments = true,
            };
            return new RuntimeDefinedParameter(name, typeof(string[]),
                new System.Collections.ObjectModel.Collection<System.Attribute> { attr });
        }

        private static TestableParameterBinderController BuildController(params RuntimeDefinedParameter[] parameters)
        {
            var meta = BindingTestFactory.BuildMetadata(parameters);
            return BindingTestFactory.CreateController(meta);
        }

        [Fact]
        public void PositionalOverflow_RemainsInUnboundArguments()
        {
            // After positional binding, extra positional args that did not match
            // any positional slot stay in the arguments list (available for VRA).
            var controller = BuildController(MakeParam("First", position: 0));
            var overflow1 = BindingTestFactory.MakePositionalArg("b");
            var overflow2 = BindingTestFactory.MakePositionalArg("c");
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                overflow1,
                overflow2,
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // "a" was bound to First, "b" and "c" remain
            Assert.Equal(2, args.Count);
            Assert.Equal("b", args[0].ArgumentValue);
            Assert.Equal("c", args[1].ArgumentValue);
        }

        [Fact]
        public void AllBound_NothingOverflows()
        {
            // When exactly enough positional args are supplied, the list is empty after binding.
            var controller = BuildController(
                MakeParam("First", position: 0),
                MakeParam("Second", position: 1));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                BindingTestFactory.MakePositionalArg("b"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            Assert.Empty(args);
        }

        [Fact]
        public void VRAParam_ExcludedFromPositionalDictionary()
        {
            // A VRA (ValueFromRemainingArguments) parameter is NOT included in the
            // positional dictionary and therefore does NOT consume positional args.
            var controller = BuildController(
                MakeParam("First", position: 0),
                MakeVRAParam("Rest"));           // VRA param — should be skipped by positional binding
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                BindingTestFactory.MakePositionalArg("b"),  // overflow for VRA
                BindingTestFactory.MakePositionalArg("c"),  // overflow for VRA
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // "a" consumed by First; "b" and "c" should overflow (not bound by VRA positionally)
            Assert.Single(controller.DispatchCalls);   // only First was dispatched
            Assert.Equal("First", controller.DispatchCalls[0].ParamName);
            Assert.Equal(2, args.Count);    // "b" and "c" remain unbound for HandleRemainingArguments
        }

        [Fact]
        public void UnknownNamedArg_StaysAfterPositionalBinding()
        {
            // A named arg with an unrecognized parameter name stays in the argument list
            // after positional binding (it's not positional, so positional bind skips it).
            var controller = BuildController(MakeParam("Known", position: 0));
            var unknownNamed = BindingTestFactory.MakeNamedArg("Unknown", "val");
            var args = new List<CommandParameterInternal>
            {
                unknownNamed,
                BindingTestFactory.MakePositionalArg("positional"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // The unknown named arg should still be in the list
            Assert.Contains(unknownNamed, args);
        }

        [Fact]
        public void MultipleOverflowArgs_PreserveOrder()
        {
            // Multiple overflow positional args preserve their input order in the list.
            var controller = BuildController(MakeParam("First", position: 0));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("first"),
                BindingTestFactory.MakePositionalArg("overflow1"),
                BindingTestFactory.MakePositionalArg("overflow2"),
                BindingTestFactory.MakePositionalArg("overflow3"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // "first" was bound, the rest overflow
            Assert.Equal(3, args.Count);
            Assert.Equal("overflow1", args[0].ArgumentValue);
            Assert.Equal("overflow2", args[1].ArgumentValue);
            Assert.Equal("overflow3", args[2].ArgumentValue);
        }

        [Fact]
        public void VRAParam_StaysInUnboundParameters_AfterPositionalBinding()
        {
            // VRA param is not consumed by positional binding, so it should
            // remain in UnboundParameters after the positional pass —
            // ready for HandleRemainingArguments to find and bind overflow.
            var vra = MakeVRAParam("Rest");
            var controller = BuildController(MakeParam("First", position: 0), vra);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                BindingTestFactory.MakePositionalArg("b"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // "First" was bound (removed from UnboundParameters by the stub)
            // "Rest" was NOT bound (not a positional parameter) → still in UnboundParameters
            var unbound = controller.GetBoundParameters();
            Assert.False(unbound.ContainsKey("Rest"));
        }

        [Fact]
        public void EmptyArgList_NoOverflow_NothingUnbound()
        {
            // With no positional args at all, the arguments list is empty before and after binding.
            var controller = BuildController(MakeParam("First", position: 0));
            var args = new List<CommandParameterInternal>();

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            Assert.Empty(args);
            Assert.Empty(controller.DispatchCalls);
        }

        [Fact]
        public void NoPositionalParams_AllArgsAreOverflow()
        {
            // When metadata has no positional parameters, ALL positional args
            // in the arguments list remain as overflow for HandleRemainingArguments.
            var controller = BuildController(MakeParam("Named"));  // no position assigned
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("x"),
                BindingTestFactory.MakePositionalArg("y"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            Assert.Empty(controller.DispatchCalls);
            Assert.Equal(2, args.Count);
        }
    }
}
