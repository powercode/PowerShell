// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Language;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Stub implementing <see cref="IDefaultValueManagerContext"/> for direct unit tests.
    /// </summary>
    internal sealed class TestDefaultValueManagerContext : IDefaultValueManagerContext
    {
        public InvocationInfo InvocationInfo { get; set; } =
            new InvocationInfo(new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);

        public Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public IList<MergedCompiledCommandParameter> UnboundParameters { get; set; } =
            new List<MergedCompiledCommandParameter>();

        public Dictionary<string, CommandParameterInternal> BoundArguments { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Values the stub returns from <see cref="GetDefaultParameterValue"/>.</summary>
        public Dictionary<string, object> DefaultValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tracks every call to <see cref="RestoreParameter"/>.</summary>
        public List<(CommandParameterInternal Argument, MergedCompiledCommandParameter Parameter)> RestoreCalls { get; } = new();

        public IScriptExtent GetErrorExtent(CommandParameterInternal argument) => PositionUtilities.EmptyExtent;

        public object GetDefaultParameterValue(string name)
        {
            DefaultValues.TryGetValue(name, out var value);
            return value;
        }

        public bool RestoreParameter(CommandParameterInternal argument, MergedCompiledCommandParameter parameter)
        {
            RestoreCalls.Add((argument, parameter));
            return true;
        }

        public void ReturnPipelineCpi(CommandParameterInternal cpi) { /* no-op in tests */ }
    }

    internal static class DefaultValueManagerTestHelper
    {
        internal static MergedCompiledCommandParameter MakeParameter(string name, Type type = null)
        {
            type ??= typeof(string);
            var rdp = new RuntimeDefinedParameter(name, type, new Collection<Attribute> { new ParameterAttribute() });
            var compiled = new CompiledCommandParameter(rdp, false);
            return new MergedCompiledCommandParameter(compiled, ParameterBinderAssociation.DeclaredFormalParameters);
        }
    }

    /// <summary>
    /// Direct unit tests for <see cref="DefaultValueManager"/> exercising
    /// SaveScriptParameterValue, Backup, and Restore through a stub context.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class DefaultValueManagerTests
    {
        [Fact]
        public void SaveScriptParameterValue_StoresValue_RestoredToParameter()
        {
            var ctx = new TestDefaultValueManagerContext();
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Label");

            mgr.SaveScriptParameterValue("Label", "-Label:", "default-label");

            // Simulate the parameter being bound (so Restore moves it to unbound).
            ctx.BoundParameters["Label"] = param;

            mgr.Restore(new[] { param });

            Assert.Single(ctx.RestoreCalls);
            Assert.Equal("default-label", ctx.RestoreCalls[0].Argument.ArgumentValue);
            // Restore should remove from BoundParameters and add to UnboundParameters.
            Assert.DoesNotContain("Label", ctx.BoundParameters.Keys);
            Assert.Contains(param, ctx.UnboundParameters);
        }

        [Fact]
        public void Backup_SavesCurrentDefault_WhenNotAlreadySaved()
        {
            var ctx = new TestDefaultValueManagerContext();
            ctx.DefaultValues["Base"] = 10;
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Base", typeof(int));

            mgr.Backup(param);

            // After backup, Restore should push the original default through RestoreParameter.
            ctx.BoundParameters["Base"] = param;
            mgr.Restore(new[] { param });

            Assert.Single(ctx.RestoreCalls);
            Assert.Equal(10, ctx.RestoreCalls[0].Argument.ArgumentValue);
        }

        [Fact]
        public void Backup_SkipsAlreadySaved_WhenSaveScriptParameterValueCalledFirst()
        {
            var ctx = new TestDefaultValueManagerContext();
            ctx.DefaultValues["Label"] = "from-backup";
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Label");

            // SaveScriptParameterValue takes priority.
            mgr.SaveScriptParameterValue("Label", "-Label:", "from-save");
            mgr.Backup(param);

            ctx.BoundParameters["Label"] = param;
            mgr.Restore(new[] { param });

            Assert.Single(ctx.RestoreCalls);
            // Should use the SaveScriptParameterValue value, not the Backup value.
            Assert.Equal("from-save", ctx.RestoreCalls[0].Argument.ArgumentValue);
        }

        [Fact]
        public void Restore_MovesParameterFromBoundToUnbound()
        {
            var ctx = new TestDefaultValueManagerContext();
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Name");

            mgr.SaveScriptParameterValue("Name", "-Name:", "default");
            ctx.BoundParameters["Name"] = param;

            mgr.Restore(new[] { param });

            Assert.DoesNotContain("Name", ctx.BoundParameters.Keys);
            Assert.Contains(param, ctx.UnboundParameters);
            Assert.DoesNotContain("Name", (IDictionary<string, CommandParameterInternal>)ctx.BoundArguments);
        }

        [Fact]
        public void Restore_KeepsParameterInBound_WhenNoSavedDefault()
        {
            var ctx = new TestDefaultValueManagerContext();
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Extra");

            // No SaveScriptParameterValue or Backup for "Extra".
            // Restore should ensure it ends up in BoundParameters.
            mgr.Restore(new[] { param });

            Assert.Contains("Extra", ctx.BoundParameters.Keys);
            Assert.DoesNotContain(param, ctx.UnboundParameters);
            Assert.Empty(ctx.RestoreCalls);
        }

        [Fact]
        public void Restore_ThrowsArgumentNullException_ForNullParameters()
        {
            var ctx = new TestDefaultValueManagerContext();
            var mgr = new DefaultValueManager(ctx);

            Assert.Throws<PSArgumentNullException>(() => mgr.Restore(null));
        }

        [Fact]
        public void Restore_SkipsNullEntries_InParameterList()
        {
            var ctx = new TestDefaultValueManagerContext();
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("OK");

            mgr.SaveScriptParameterValue("OK", "-OK:", "val");
            ctx.BoundParameters["OK"] = param;

            // null entry should be silently skipped.
            mgr.Restore(new MergedCompiledCommandParameter[] { null, param });

            Assert.Single(ctx.RestoreCalls);
            Assert.Equal("val", ctx.RestoreCalls[0].Argument.ArgumentValue);
        }

        [Fact]
        public void Backup_NullDefault_SavedAndRestoredAsNull()
        {
            var ctx = new TestDefaultValueManagerContext();
            // DefaultValues doesn't contain "Tag", so GetDefaultParameterValue returns null.
            var mgr = new DefaultValueManager(ctx);
            var param = DefaultValueManagerTestHelper.MakeParameter("Tag");

            mgr.Backup(param);

            ctx.BoundParameters["Tag"] = param;
            mgr.Restore(new[] { param });

            Assert.Single(ctx.RestoreCalls);
            Assert.Null(ctx.RestoreCalls[0].Argument.ArgumentValue);
        }
    }
}
