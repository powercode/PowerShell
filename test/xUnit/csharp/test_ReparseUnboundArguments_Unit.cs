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
    /// Unit tests for the <c>ReparseUnboundArguments</c> algorithm on
    /// <see cref="ParameterBinderController"/>. All tests operate directly on
    /// <see cref="TestableParameterBinderController.GetUnboundArguments"/> without
    /// <c>PowerShell.Create()</c>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class ReparseUnboundArgumentsUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name, System.Type type = null)
        {
            type ??= typeof(string);
            return new RuntimeDefinedParameter(name, type,
                new Collection<Attribute>
                {
                    new ParameterAttribute { ParameterSetName = ParameterAttribute.AllParameterSets },
                });
        }

        private static RuntimeDefinedParameter MakeSwitchParam(string name)
        {
            return new RuntimeDefinedParameter(name, typeof(SwitchParameter),
                new Collection<Attribute>
                {
                    new ParameterAttribute { ParameterSetName = ParameterAttribute.AllParameterSets },
                });
        }

        // Helper to set up the controller with initial unbound arguments and call reparse
        private static List<CommandParameterInternal> SetUpAndReparse(
            TestableParameterBinderController controller,
            List<CommandParameterInternal> arguments)
        {
            controller.CallInitUnboundArguments(arguments);
            controller.CallReparseUnboundArguments();
            return controller.GetUnboundArguments();
        }

        [Fact]
        public void ParamFollowedByValue_PairedCorrectly()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Name"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Name", "-Name"),
                CommandParameterInternal.CreateArgument("alpha"),
            });

            Assert.Single(result);
            Assert.Equal("Name", result[0].ParameterName);
            Assert.Equal("alpha", result[0].ArgumentValue);
        }

        [Fact]
        public void SwitchParam_NoFollowingValue_DefaultsToPresent()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeSwitchParam("Force"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Force", "-Force"),
            });

            Assert.Single(result);
            Assert.Equal("Force", result[0].ParameterName);
            Assert.Equal(SwitchParameter.Present, result[0].ArgumentValue);
        }

        [Fact]
        public void SwitchParam_ExplicitFalse_ColonSyntax()
        {
            // When the user writes -Force:$false, the parser already pairs them as one CPI
            // with ParameterNameSpecified=true and ArgumentSpecified=true; reparse passes it through.
            var metadata = BindingTestFactory.BuildMetadata(MakeSwitchParam("Force"));
            var controller = BindingTestFactory.CreateController(metadata);

            // Already-paired: the colon syntax produces a CreateParameterWithArgument CPI
            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameterWithArgument(
                    null, "Force", "-Force:", null, false, false),
            });

            Assert.Single(result);
            Assert.Equal("Force", result[0].ParameterName);
            Assert.Equal(false, result[0].ArgumentValue);
        }

        [Fact]
        public void TrailingParamWithNoValue_ThrowsMissingArgument()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Name"));
            var controller = BindingTestFactory.CreateController(metadata);
            controller.CallInitUnboundArguments(new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Name", "-Name"),
            });

            Assert.Throws<ParameterBindingException>(() =>
                controller.CallReparseUnboundArguments());
        }

        [Fact]
        public void ParamFollowedByKnownParam_ThrowsMissingArgument()
        {
            // -Name followed by -Other where both are known params
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Name"), MakeParam("Other"));
            var controller = BindingTestFactory.CreateController(metadata);
            controller.CallInitUnboundArguments(new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Name", "-Name"),
                CommandParameterInternal.CreateParameter("Other", "-Other"),
            });

            Assert.Throws<ParameterBindingException>(() =>
                controller.CallReparseUnboundArguments());
        }

        [Fact]
        public void ColonSyntax_AlreadyPaired_PassesThrough()
        {
            // -Name:hello is already a paired CPI; ReparseUnboundArguments leaves it unchanged
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Name"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameterWithArgument(
                    null, "Name", "-Name:", null, "hello", false),
            });

            Assert.Single(result);
            Assert.Equal("Name", result[0].ParameterName);
            Assert.Equal("hello", result[0].ArgumentValue);
        }

        [Fact]
        public void SwitchThenNamedParam_SwitchGetsPresent()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeSwitchParam("Force"), MakeParam("Name"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Force", "-Force"),
                CommandParameterInternal.CreateParameter("Name", "-Name"),
                CommandParameterInternal.CreateArgument("value"),
            });

            Assert.Equal(2, result.Count);
            var forceArg = result[0];
            var nameArg = result[1];
            Assert.Equal("Force", forceArg.ParameterName);
            Assert.Equal(SwitchParameter.Present, forceArg.ArgumentValue);
            Assert.Equal("Name", nameArg.ParameterName);
            Assert.Equal("value", nameArg.ArgumentValue);
        }

        [Fact]
        public void UnknownParam_RemainsUnpaired()
        {
            // -UnknownParam is not in metadata → both CPIs survive unchanged
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("UnknownParam", "-UnknownParam"),
                CommandParameterInternal.CreateArgument("value"),
            });

            // Both CPIs remain since -UnknownParam can't be resolved
            Assert.Equal(2, result.Count);
            Assert.False(result[0].ArgumentSpecified);
        }

        [Fact]
        public void DashLiteralAsValue_TreatedAsValue()
        {
            // -Sep followed by a plain argument that happens to be "-" (a dash literal)
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Sep"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Sep", "-Sep"),
                CommandParameterInternal.CreateArgument("-"),
            });

            Assert.Single(result);
            Assert.Equal("Sep", result[0].ParameterName);
            Assert.Equal("-", result[0].ArgumentValue);
        }

        [Fact]
        public void MultipleParamsAndValues_AllPairedCorrectly()
        {
            var metadata = BindingTestFactory.BuildMetadata(
                MakeParam("Name"),
                MakeParam("Path"),
                MakeSwitchParam("Force"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameter("Name", "-Name"),
                CommandParameterInternal.CreateArgument("alpha"),
                CommandParameterInternal.CreateParameter("Path", "-Path"),
                CommandParameterInternal.CreateArgument("/tmp"),
                CommandParameterInternal.CreateParameter("Force", "-Force"),
            });

            Assert.Equal(3, result.Count);
            Assert.Equal("Name", result[0].ParameterName);
            Assert.Equal("alpha", result[0].ArgumentValue);
            Assert.Equal("Path", result[1].ParameterName);
            Assert.Equal("/tmp", result[1].ArgumentValue);
            Assert.Equal("Force", result[2].ParameterName);
            Assert.Equal(SwitchParameter.Present, result[2].ArgumentValue);
        }

        [Fact]
        public void AlreadyPairedNamedArg_PassesThrough()
        {
            // A CPI with both parameter name and argument (e.g. from MakeNamedArg) passes through unchanged
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);

            var result = SetUpAndReparse(controller, new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "hello"),
            });

            Assert.Single(result);
            Assert.Equal("Path", result[0].ParameterName);
            Assert.Equal("hello", result[0].ArgumentValue);
        }
    }
}
