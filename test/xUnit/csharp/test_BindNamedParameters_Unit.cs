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
    /// Unit tests for the <c>BindNamedParameters</c> algorithm on
    /// <see cref="ParameterBinderController"/>. All tests exercise the real
    /// protected algorithm via <see cref="TestableParameterBinderController"/>
    /// without <c>PowerShell.Create()</c>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class BindNamedParametersUnitTests
    {
        private static RuntimeDefinedParameter MakeParam(string name, Type type = null, string alias = null, string setName = null)
        {
            type ??= typeof(string);
            var attrs = new Collection<Attribute>
            {
                new ParameterAttribute
                {
                    Position = -1,
                    ParameterSetName = setName ?? ParameterAttribute.AllParameterSets,
                }
            };
            if (alias != null)
            {
                attrs.Add(new AliasAttribute(alias));
            }

            return new RuntimeDefinedParameter(name, type, attrs);
        }

        [Fact]
        public void ExactNameMatch_BindsParameter()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "hello"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Path", controller.DispatchCalls[0].ParamName);
            Assert.Equal("hello", controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void PrefixMatch_BindsParameter()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Pa", "hello"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Path", controller.DispatchCalls[0].ParamName);
        }

        [Fact]
        public void AmbiguousPrefix_ThrowsParameterBindingException()
        {
            // "P" matches both "Path" and "Process" — BindNamedParameters throws for ambiguous prefix
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"), MakeParam("Process"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("P", "value"),
            };

            var ex = Assert.Throws<ParameterBindingException>(() =>
                controller.BindNamedParameters(uint.MaxValue, args));
            Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AliasMatch_BindsParameter()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("ComputerName", alias: "CN"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("CN", "server1"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("ComputerName", controller.DispatchCalls[0].ParamName);
            Assert.Equal("server1", controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void UnknownParameterName_RemainsUnbound()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("NonExistent", "value"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Empty(controller.DispatchCalls);
            // Unknown argument remains in the list for later processing
            Assert.Single(args);
        }

        [Fact]
        public void CaseInsensitiveMatch_BindsParameter()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("path", "hello"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Path", controller.DispatchCalls[0].ParamName);
        }

        [Fact]
        public void AlreadyBoundParameter_Throws()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            // Pre-populate BoundParameters via a first bind
            var firstArgs = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "first"),
            };
            controller.BindNamedParameters(uint.MaxValue, firstArgs);
            Assert.Single(controller.DispatchCalls);

            // Try to bind the same parameter again
            var secondArgs = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "second"),
            };
            Assert.Throws<ParameterBindingException>(() =>
                controller.BindNamedParameters(uint.MaxValue, secondArgs));
        }

        [Fact]
        public void MultipleNamedParameters_AllBind()
        {
            var switchAttrs = new Collection<Attribute>
            {
                new ParameterAttribute { ParameterSetName = ParameterAttribute.AllParameterSets },
            };
            var forceParam = new RuntimeDefinedParameter("Force", typeof(SwitchParameter), switchAttrs);
            var metadata = BindingTestFactory.BuildMetadata(
                MakeParam("Path"),
                MakeParam("Name"),
                forceParam);
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "/tmp"),
                BindingTestFactory.MakeNamedArg("Name", "foo"),
                BindingTestFactory.MakeNamedArg("Force", SwitchParameter.Present),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Equal(3, controller.DispatchCalls.Count);
        }

        [Fact]
        public void SplattedNamedArg_BindsNormally()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeSplattedNamedArg("Path", "hello"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Path", controller.DispatchCalls[0].ParamName);
            Assert.Equal("hello", controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void NamedArgWithNullValue_BindsNull()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", null),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Single(controller.DispatchCalls);
            Assert.Equal("Path", controller.DispatchCalls[0].ParamName);
            Assert.Null(controller.DispatchCalls[0].Value);
        }

        [Fact]
        public void EmptyArgumentList_NoDispatchCalls()
        {
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>();

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Empty(controller.DispatchCalls);
        }

        [Fact]
        public void PositionalArgWithNoName_SkippedByNamedBinding()
        {
            // BindNamedParameters only processes args with ParameterNameSpecified
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);
            var args = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakePositionalArg("positional-value"),
            };

            controller.BindNamedParameters(uint.MaxValue, args);

            Assert.Empty(controller.DispatchCalls);
            // Positional arg remains for later positional binding
            Assert.Single(args);
        }

        [Fact]
        public void SplattedArgSupersededByExplicit_SplattedRemoved()
        {
            // When an explicit named arg is already bound, a splatted arg for the same
            // parameter gets silently removed (not double-bound).
            var metadata = BindingTestFactory.BuildMetadata(MakeParam("Path"));
            var controller = BindingTestFactory.CreateController(metadata);

            // Bind explicit arg first
            var firstArgs = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeNamedArg("Path", "explicit"),
            };
            controller.BindNamedParameters(uint.MaxValue, firstArgs);
            Assert.Single(controller.DispatchCalls);

            // Now supply a splatted arg for the same param in a second call
            var splattedArgs = new List<CommandParameterInternal>
            {
                BindingTestFactory.MakeSplattedNamedArg("Path", "splatted"),
            };

            // Should silently drop the splatted duplicate rather than throwing
            controller.BindNamedParameters(uint.MaxValue, splattedArgs);

            // Still only 1 dispatch (splatted was removed, not dispatched)
            Assert.Single(controller.DispatchCalls);
            Assert.Empty(splattedArgs);
        }
    }
}
