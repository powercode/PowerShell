// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Unit tests for delay-bind deferral decision logic by validating
    /// <c>CmdletParameterBinderController.IsParameterScriptBlockBindable</c>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class DelayBindScriptBlockUnitTests
    {
        private static readonly MethodInfo s_isParameterScriptBlockBindable =
            typeof(CmdletParameterBinderController)
                .GetMethod("IsParameterScriptBlockBindable", BindingFlags.NonPublic | BindingFlags.Static);

        private static bool IsScriptBlockBindable(RuntimeDefinedParameter parameter)
        {
            var metadata = BindingTestFactory.BuildMetadata(parameter);
            MergedCompiledCommandParameter merged = metadata.BindableParameters[parameter.Name];
            return (bool)s_isParameterScriptBlockBindable.Invoke(obj: null, parameters: new object[] { merged });
        }

        [Fact]
        public void ScriptBlockToFileInfoParam_WithVFP_IsNotScriptBlockBindable()
        {
            // FileInfo is not ScriptBlock-compatible, so ScriptBlock args should be delay-bound
            // when DelayBindScriptBlock is enabled and the param takes pipeline input.
            var param = ParameterSetResolverTestFactory.MakeParam(
                "Path",
                type: typeof(System.IO.FileInfo),
                valueFromPipeline: true);

            bool result = IsScriptBlockBindable(param);

            Assert.False(result);
        }

        [Fact]
        public void ScriptBlockToObjectParam_IsScriptBlockBindable()
        {
            var param = ParameterSetResolverTestFactory.MakeParam(
                "InputObject",
                type: typeof(object),
                valueFromPipeline: true);

            bool result = IsScriptBlockBindable(param);

            Assert.True(result);
        }

        [Fact]
        public void ScriptBlockToScriptBlockParam_IsScriptBlockBindable()
        {
            var param = ParameterSetResolverTestFactory.MakeParam(
                "Script",
                type: typeof(ScriptBlock),
                valueFromPipeline: true);

            bool result = IsScriptBlockBindable(param);

            Assert.True(result);
        }
    }
}
