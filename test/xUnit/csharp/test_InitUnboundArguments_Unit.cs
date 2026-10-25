// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for the <c>InitUnboundArguments</c> algorithm on
    /// <see cref="ParameterBinderController"/>. The method separates splatted
    /// (hashtable-sourced) arguments to the end of <c>UnboundArguments</c> so
    /// that explicitly-specified named arguments can supersede them.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class InitUnboundArgumentsUnitTests
    {
        private static TestableParameterBinderController CreateController()
        {
            var metadata = BindingTestFactory.BuildMetadata(
                ParameterSetResolverTestFactory.MakeParam("A"),
                ParameterSetResolverTestFactory.MakeParam("B"),
                ParameterSetResolverTestFactory.MakeParam("C"));
            return BindingTestFactory.CreateController(metadata);
        }

        [Fact]
        public void NonSplattedArgs_AppendedInOrder()
        {
            var controller = CreateController();
            var a = BindingTestFactory.MakeNamedArg("A", "1");
            var b = BindingTestFactory.MakeNamedArg("B", "2");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { a, b });
            var result = controller.GetUnboundArguments();

            Assert.Equal(2, result.Count);
            Assert.Same(a, result[0]);
            Assert.Same(b, result[1]);
        }

        [Fact]
        public void SplattedArgs_MovedToEndAfterRegularArgs()
        {
            var controller = CreateController();
            var regular = BindingTestFactory.MakeNamedArg("A", "regular");
            var splatted = BindingTestFactory.MakeSplattedNamedArg("B", "splatted");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { splatted, regular });
            var result = controller.GetUnboundArguments();

            Assert.Equal(2, result.Count);
            // Regular arg must come first regardless of input order
            Assert.Same(regular, result[0]);
            // Splatted arg is pushed to the end
            Assert.Same(splatted, result[1]);
        }

        [Fact]
        public void AllSplatted_AllMovedToEnd_OrderPreserved()
        {
            var controller = CreateController();
            var s1 = BindingTestFactory.MakeSplattedNamedArg("A", "s1");
            var s2 = BindingTestFactory.MakeSplattedNamedArg("B", "s2");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { s1, s2 });
            var result = controller.GetUnboundArguments();

            Assert.Equal(2, result.Count);
            // Splatted args maintain their relative order
            Assert.Same(s1, result[0]);
            Assert.Same(s2, result[1]);
        }

        [Fact]
        public void MultipleSplattedAndRegular_RegularFirstThenSplatted()
        {
            var controller = CreateController();
            var r1 = BindingTestFactory.MakeNamedArg("A", "r1");
            var s1 = BindingTestFactory.MakeSplattedNamedArg("B", "s1");
            var r2 = BindingTestFactory.MakeNamedArg("C", "r2");
            var s2 = BindingTestFactory.MakeSplattedNamedArg("A", "s2");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { s1, r1, s2, r2 });
            var result = controller.GetUnboundArguments();

            Assert.Equal(4, result.Count);
            // Regular args come first, splatted args come after
            Assert.Same(r1, result[0]);
            Assert.Same(r2, result[1]);
            Assert.Same(s1, result[2]);
            Assert.Same(s2, result[3]);
        }

        [Fact]
        public void EmptyList_UnboundArgumentsRemainsEmpty()
        {
            var controller = CreateController();

            controller.CallInitUnboundArguments(new List<CommandParameterInternal>());

            Assert.Empty(controller.GetUnboundArguments());
        }

        [Fact]
        public void CalledTwice_AppendsToExistingUnboundArguments()
        {
            // InitUnboundArguments appends — it does not clear UnboundArguments first.
            // Two calls accumulate both batches of arguments.
            var controller = CreateController();
            var first = BindingTestFactory.MakeNamedArg("A", "first");
            var second = BindingTestFactory.MakeNamedArg("B", "second");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { first });
            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { second });
            var result = controller.GetUnboundArguments();

            Assert.Equal(2, result.Count);
            Assert.Same(first, result[0]);
            Assert.Same(second, result[1]);
        }

        [Fact]
        public void SplattedInterleaved_AllRegularBeforeAllSplatted()
        {
            // Even when splatted args appear between regular args in the input,
            // all regular args are ordered before all splatted args in the output.
            var controller = CreateController();
            var r = BindingTestFactory.MakeNamedArg("A", "regular");
            var s = BindingTestFactory.MakeSplattedNamedArg("B", "splatted");

            controller.CallInitUnboundArguments(new List<CommandParameterInternal> { s, r });
            var result = controller.GetUnboundArguments();

            // Index 0 is always the regular arg
            Assert.False(result[0].FromHashtableSplatting);
            // Index 1 is always the splatted arg
            Assert.True(result[1].FromHashtableSplatting);
        }
    }
}
