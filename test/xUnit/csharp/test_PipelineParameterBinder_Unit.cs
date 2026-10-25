// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for <see cref="PipelineParameterBinder"/> using
    /// <see cref="RichTestBindingContext"/> as a lightweight binding state/ops stub.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class PipelineParameterBinderUnitTests
    {
        private static RuntimeDefinedParameter MakeVfpParam(string name, Type type = null)
            => ParameterSetResolverTestFactory.MakeParam(name, type: type, valueFromPipeline: true);

        private static RuntimeDefinedParameter MakeVfpByNameParam(string name, Type type = null)
            => ParameterSetResolverTestFactory.MakeParam(name, type: type, valueFromPipelineByPropertyName: true);

        private static RuntimeDefinedParameter MakeAliasVfpByNameParam(string name, string alias)
        {
            var attrs = new Collection<Attribute>
            {
                new AliasAttribute(alias),
                new ParameterAttribute { ValueFromPipelineByPropertyName = true },
            };
            return new RuntimeDefinedParameter(name, typeof(string), attrs);
        }

        private static (RichTestBindingContext Ctx, PipelineParameterBinder Binder) CreatePipelineBinder(params RuntimeDefinedParameter[] parameters)
        {
            var metadata = BindingTestFactory.BuildMetadata(parameters);
            var ctx = new RichTestBindingContext();
            ctx.InitializePipelineState(metadata);
            var binder = new PipelineParameterBinder(ctx, ctx);
            return (ctx, binder);
        }

        [Fact]
        public void ValueFromPipeline_NoCoercion_BindsDirectly()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpParam("Value", type: typeof(int)));

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(42));

            Assert.True(result);
            Assert.Single(ctx.BindCalls);
            Assert.Equal("Value", ctx.BindCalls[0].ParamName);
            Assert.Equal(42, ((PSObject)ctx.BindCalls[0].Value).BaseObject);
        }

        [Fact]
        public void ValueFromPipelineByPropertyName_BindsMatchingProperty()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpByNameParam("Name", type: typeof(string)));

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(new { Name = "alpha" }));

            Assert.True(result);
            Assert.Single(ctx.BindCalls);
            Assert.Equal("Name", ctx.BindCalls[0].ParamName);
            Assert.Equal("alpha", ctx.BindCalls[0].Value);
        }

        [Fact]
        public void ValueFromPipelineByPropertyName_UsesAlias()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeAliasVfpByNameParam("ComputerName", "CN"));

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(new { CN = "server01" }));

            Assert.True(result);
            Assert.Single(ctx.BindCalls);
            Assert.Equal("ComputerName", ctx.BindCalls[0].ParamName);
            Assert.Equal("server01", ctx.BindCalls[0].Value);
        }

        [Fact]
        public void NoMatchingProperty_ReturnsFalse()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpByNameParam("Name", type: typeof(string)));

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(new { Other = 1 }));

            Assert.False(result);
            Assert.Empty(ctx.BindCalls);
        }

        [Fact]
        public void CoercionFallthrough_WhenVfpFails_ThenByPropertyNameBinds()
        {
            var (ctx, binder) = CreatePipelineBinder(
                MakeVfpParam("Value", type: typeof(int)),
                MakeVfpByNameParam("Count", type: typeof(int)));

            ctx.OnDispatchBind = (argument, parameter) =>
            {
                // Simulate first candidate failing and second succeeding.
                if (string.Equals(parameter.Parameter.Name, "Value", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                ctx.BoundArguments[parameter.Parameter.Name] = argument;
                return true;
            };

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(new { Count = 5 }));

            Assert.True(result);
            Assert.Contains(ctx.BindCalls, c => c.ParamName == "Value");
            Assert.Contains(ctx.BindCalls, c => c.ParamName == "Count");
        }

        [Fact]
        public void MultipleObjects_RestoresDefaultsBetweenObjects()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpParam("Value", type: typeof(int)));
            ctx.OnDispatchBind = (argument, parameter) => true;

            bool first = binder.BindPipelineParameters(PSObject.AsPSObject(1));
            bool second = binder.BindPipelineParameters(PSObject.AsPSObject(2));

            Assert.True(first);
            Assert.True(second);
            Assert.NotEmpty(ctx.RestoredParameters);
        }

        [Fact]
        public void DelayBindHandled_SkipsPipelineBindingLoop()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpParam("Value", type: typeof(int)));
            ctx.OnInvokeDelayBind = _ => (Result: true, ThereWasSomethingToBind: true);

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(123));

            Assert.True(result);
            // Successful delay-bind does not short-circuit binding; pipeline binding can still run.
            Assert.Single(ctx.BindCalls);
        }

        [Fact]
        public void DelayBindFailure_ReturnsFalse()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpParam("Value", type: typeof(int)));
            ctx.OnInvokeDelayBind = _ => (Result: false, ThereWasSomethingToBind: true);

            bool result = binder.BindPipelineParameters(PSObject.AsPSObject(123));

            Assert.False(result);
            Assert.Empty(ctx.BindCalls);
        }

        [Fact]
        public void ResetPipelinePlan_AllowsRebindingAfterPreviousBind()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpParam("Value", type: typeof(int)));
            ctx.OnDispatchBind = (argument, parameter) => true;

            Assert.True(binder.BindPipelineParameters(PSObject.AsPSObject(1)));
            binder.ResetPipelinePlan();
            Assert.True(binder.BindPipelineParameters(PSObject.AsPSObject(2)));
        }

        [Fact]
        public void EmptyInputObject_NoMatch_ReturnsFalse()
        {
            var (ctx, binder) = CreatePipelineBinder(MakeVfpByNameParam("Name", type: typeof(string)));

            bool result = binder.BindPipelineParameters(new PSObject());

            Assert.False(result);
            Assert.Empty(ctx.BindCalls);
        }
    }
}
