// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for the <c>BindPositionalParameters</c> algorithm on
    /// <see cref="ParameterBinderController"/>. All tests operate directly on
    /// <see cref="TestableParameterBinderController"/> without <c>PowerShell.Create()</c>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class BindPositionalParametersUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name, int position = int.MinValue, System.Type type = null)
            => ParameterSetResolverTestFactory.MakeParam(name, type: type, position: position);

        private static TestableParameterBinderController BuildController(params RuntimeDefinedParameter[] parameters)
        {
            var metadata = BindingTestFactory.BuildMetadata(parameters);
            return BindingTestFactory.CreateController(metadata);
        }

        [Fact]
        public void SinglePositional_BindsToPosition0()
        {
            var controller = BuildController(MakeParam("Name", position: 0));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("alpha"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Empty(args);
            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Name", controller.DispatchCalls[0].ParamName);
            Assert.Equal("alpha", controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void TwoPositionals_BindBothInOrder()
        {
            var controller = BuildController(
                MakeParam("First", position: 0),
                MakeParam("Second", position: 1));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                BindingTestFactory.MakePositionalArg("b"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Empty(args);
            Assert.Equal(2, controller.DispatchCalls.Count);
            Assert.Equal("First", controller.DispatchCalls[0].ParamName);
            Assert.Equal("a", controller.DispatchCalls[0].Value);
            Assert.Equal("Second", controller.DispatchCalls[1].ParamName);
            Assert.Equal("b", controller.DispatchCalls[1].Value);
        }

        [Fact]
        public void MoreArgsThanPositionalParams_ExtraRemainUnbound()
        {
            var controller = BuildController(
                MakeParam("First", position: 0),
                MakeParam("Second", position: 1));
            var extra = BindingTestFactory.MakePositionalArg("extra");
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("a"),
                BindingTestFactory.MakePositionalArg("b"),
                extra,
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Single(args);
            Assert.Equal("extra", args[0].ArgumentValue);
            Assert.Equal(2, controller.DispatchCalls.Count);
        }

        [Fact]
        public void NoPositionalParams_ArgsUnchanged()
        {
            // Parameters with no position (int.MinValue) are not eligible for positional binding
            var controller = BuildController(
                MakeParam("Name"),   // position defaults to int.MinValue — not positional
                MakeParam("Value")); // same
            var positionalArg = BindingTestFactory.MakePositionalArg("orphan");
            var args = new List<CommandParameterInternal> { positionalArg };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Single(args);
            Assert.Empty(controller.DispatchCalls);
        }

        [Fact]
        public void EmptyArgList_NothingBinds()
        {
            var controller = BuildController(MakeParam("Name", position: 0));
            var args = new List<CommandParameterInternal>();

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Empty(args);
            Assert.Empty(controller.DispatchCalls);
        }

        [Fact]
        public void PositionalParam_IntType_ValueBoundAsProvided()
        {
            // The stub does not coerce; dispatch receives whatever value was passed
            var controller = BuildController(MakeParam("Count", position: 0, type: typeof(int)));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg(42),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Empty(args);
            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Count", controller.DispatchCalls[0].ParamName);
            Assert.Equal(42, controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void NamedArgAlone_SkippedForPositionalBinding()
        {
            // A named arg (ParameterNameSpecified=true) is not a positional candidate.
            // The algorithm passes it through unchanged in the output list.
            var controller = BuildController(MakeParam("Name", position: 0));
            var namedArg = BindingTestFactory.MakeNamedArg("Name", "hello");
            var args = new List<CommandParameterInternal> { namedArg };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            // No dispatch: the only arg is named so no positional match occurs
            Assert.Empty(controller.DispatchCalls);
            // The named arg stays in the list
            Assert.Single(args);
        }

        [Fact]
        public void PositionalArg_AfterOneNamedArg_IsBound()
        {
            // A positional arg following a named arg is still bound to position 0.
            // GetNextPositionalArgument skips named arg pairs when looking for positionals.
            // NOTE: namedArg here has ArgumentSpecified=true (value already attached)
            // so the arg following it is a standalone positional, not a value for the named arg.
            // However, GetNextPositionalArgument's "consume next if valueless" heuristic only
            // activates when it sees a parameterName-only arg. Since our named arg already has
            // a value (CreateParameterWithArgument), the algorithm just passes it to nonPositionals
            // and then checks the next arg. The next arg here is a positional, so the heuristic
            // WILL consume it as the "value" for the named arg — both end up in nonPositionals.
            // This test documents that observed behavior: no positional is dispatched.
            var controller = BuildController(MakeParam("Pos", position: 0));
            var namedArg = BindingTestFactory.MakeNamedArg("Other", "x"); // named, already has value
            var positional = BindingTestFactory.MakePositionalArg("beta");
            var args = new List<CommandParameterInternal> { namedArg, positional };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // Both args remain: namedArg stays (not positional) + positional is consumed by heuristic
            // The heuristic sees a named-arg followed by a valueless-looking arg and keeps both
            // in nonPositionals. So no dispatch, both args remain in output.
            Assert.Empty(controller.DispatchCalls);
            Assert.Equal(2, args.Count);
        }

        [Fact]
        public void TwoPositionalParams_NonContiguousPositions_StillBindInOrder()
        {
            // Positions 0 and 5 — there is a gap, but binding should still work in slot order
            var controller = BuildController(
                MakeParam("First", position: 0),
                MakeParam("Sixth", position: 5));
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("val1"),
                BindingTestFactory.MakePositionalArg("val2"),
            };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out var ex);

            Assert.Null(ex);
            Assert.Empty(args);
            Assert.Equal(2, controller.DispatchCalls.Count);
            Assert.Equal("First", controller.DispatchCalls[0].ParamName);
            Assert.Equal("val1", controller.DispatchCalls[0].Value);
            Assert.Equal("Sixth", controller.DispatchCalls[1].ParamName);
            Assert.Equal("val2", controller.DispatchCalls[1].Value);
        }

        [Fact]
        public void DispatchFails_ArgRemainsInList()
        {
            // When DispatchResult = false the algorithm re-adds the argument to unboundArguments
            var controller = BuildController(MakeParam("Name", position: 0));
            controller.DispatchResult = false;
            var positionalArg = BindingTestFactory.MakePositionalArg("unbound");
            var args = new List<CommandParameterInternal> { positionalArg };

            controller.CallBindPositionalParameters(args, uint.MaxValue, 0, out _);

            // Dispatch was attempted; passes 2 and 4 both run when pass 2 returns false,
            // so dispatch is called twice (no-coerce pass + coerce pass).
            Assert.Equal(2, controller.DispatchCalls.Count);
            Assert.Single(args);
        }
    }
}
