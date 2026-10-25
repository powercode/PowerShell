// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Language;
using Xunit;

namespace PSTests.Parallel
{
    // ---------------------------------------------------------------------------
    // ArgumentLooksLikeParameter tests
    // ---------------------------------------------------------------------------
    public static class ArgumentLooksLikeParameterTests
    {
        [Theory]
        [InlineData("-Foo",    true)]
        [InlineData("-",       true)]
        [InlineData("--",      true)]
        [InlineData("-123",    true)]
        [InlineData("Foo",     false)]
        [InlineData("",        false)]
        [InlineData(" -Foo",   false)]   // leading space — not a dash-first string
        public static void DetectsParameterLookingStrings(string arg, bool expected)
        {
            Assert.Equal(expected, ParameterBinderController.ArgumentLooksLikeParameter(arg));
        }

        [Fact]
        public static void Null_ReturnsFalse()
        {
            Assert.False(ParameterBinderController.ArgumentLooksLikeParameter(null));
        }

        [Fact]
        public static void EmDashIsTreatedAsDash_ReturnsTrue()
        {
            // PowerShell's IsDash() extension treats U+2014 EM DASH as a dash character.
            Assert.True(ParameterBinderController.ArgumentLooksLikeParameter("\u2014Foo"));
        }
    }

    // ---------------------------------------------------------------------------
    // IsSwitchAndSetValue tests
    // ---------------------------------------------------------------------------
    public static class IsSwitchAndSetValueTests
    {
        private static CompiledCommandParameter MakeSwitchParam(string name)
        {
            var rdp = new RuntimeDefinedParameter(name, typeof(SwitchParameter),
                new Collection<Attribute> { new ParameterAttribute() });
            return new CompiledCommandParameter(rdp, false);
        }

        private static CompiledCommandParameter MakeStringParam(string name)
        {
            var rdp = new RuntimeDefinedParameter(name, typeof(string),
                new Collection<Attribute> { new ParameterAttribute() });
            return new CompiledCommandParameter(rdp, false);
        }

        private static CompiledCommandParameter MakeBoolParam(string name)
        {
            var rdp = new RuntimeDefinedParameter(name, typeof(bool),
                new Collection<Attribute> { new ParameterAttribute() });
            return new CompiledCommandParameter(rdp, false);
        }

        [Fact]
        public static void SwitchParameter_ReturnsTrue_AndSetsArgument()
        {
            var param = CommandParameterInternal.CreateParameter("Verbose", "-Verbose");
            var ccp = MakeSwitchParam("Verbose");

            bool result = ParameterBinderController.IsSwitchAndSetValue("Verbose", param, ccp);

            Assert.True(result);
            Assert.True(param.ArgumentSpecified);
            Assert.Equal(SwitchParameter.Present, param.ArgumentValue);
        }

        [Fact]
        public static void StringParameter_ReturnsFalse_NoArgSet()
        {
            var param = CommandParameterInternal.CreateParameter("Name", "-Name");
            var ccp = MakeStringParam("Name");

            bool result = ParameterBinderController.IsSwitchAndSetValue("Name", param, ccp);

            Assert.False(result);
            Assert.False(param.ArgumentSpecified);
        }

        [Fact]
        public static void BoolParameter_ReturnsFalse()
        {
            // [bool] is NOT auto-set by IsSwitchAndSetValue — only [switch] is.
            var param = CommandParameterInternal.CreateParameter("Flag", "-Flag");
            var ccp = MakeBoolParam("Flag");

            bool result = ParameterBinderController.IsSwitchAndSetValue("Flag", param, ccp);

            Assert.False(result);
        }

        [Fact]
        public static void SwitchParameter_ArgumentNameUpdated()
        {
            // Verify that the parameterName argument is used to set param.ParameterName.
            var param = CommandParameterInternal.CreateParameter("ver", "-ver");
            var ccp = MakeSwitchParam("Verbose");

            ParameterBinderController.IsSwitchAndSetValue("Verbose", param, ccp);

            // The full resolved parameter name should now be stored on the argument.
            Assert.Equal("Verbose", param.ParameterName);
        }
    }
}
