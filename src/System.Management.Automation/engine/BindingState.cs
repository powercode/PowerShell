// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Management.Automation
{
    /// <summary>
    /// Consolidates the per-invocation mutable binding state that was previously scattered
    /// across <see cref="ParameterBinderController"/> and its SRP components.
    /// A single <see cref="BindingState"/> instance travels with each command invocation.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal sealed class BindingState
    {
        /// <summary>
        /// The name of the command being bound. Set by the controller at initialization.
        /// Used in the debugger display without requiring a reference to InvocationInfo.
        /// </summary>
        internal string? CommandName { get; set; }

        /// <summary>
        /// Parameters that have not yet been bound for the current command invocation.
        /// </summary>
        internal List<MergedCompiledCommandParameter> UnboundParameters { get; set; } = [];

        /// <summary>
        /// Parameters that have been bound, keyed by parameter name (OrdinalIgnoreCase).
        /// </summary>
        internal Dictionary<string, MergedCompiledCommandParameter> BoundParameters { get; }
            = new Dictionary<string, MergedCompiledCommandParameter>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Command-line arguments that have not yet been matched to a parameter.
        /// </summary>
        internal Collection<CommandParameterInternal> UnboundArguments { get; set; } = [];

        /// <summary>
        /// Arguments that have been bound, keyed by parameter name (OrdinalIgnoreCase).
        /// </summary>
        internal Dictionary<string, CommandParameterInternal> BoundArguments { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Names of parameters that were bound via <c>$PSDefaultParameterValues</c>.
        /// </summary>
        internal Collection<string> BoundDefaultParameters { get; } = [];

        /// <summary>
        /// Whether default parameter binding from <c>$PSDefaultParameterValues</c> is active.
        /// </summary>
        internal bool DefaultParameterBindingInUse { get; set; }

        /// <summary>
        /// Parameters that received their value from the pipeline input.
        /// </summary>
        internal Collection<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; }
            = new Collection<MergedCompiledCommandParameter>();

        // ── Task 3.1: DefaultValueManager state ──────────────────────────────────────

        /// <summary>
        /// Saved default values for restoration after each pipeline-object is processed,
        /// keyed by parameter name (OrdinalIgnoreCase).
        /// Previously stored as a field on <see cref="DefaultValueManager"/>.
        /// </summary>
        internal Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        // ── Task 3.2: DelayBindScriptBlockHandler state ───────────────────────────────

        /// <summary>
        /// ScriptBlock arguments deferred for per-pipeline-object evaluation, keyed by parameter.
        /// Previously stored as a field on <see cref="DelayBindScriptBlockHandler"/>.
        /// </summary>
        internal Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; }
            = new Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument>();

        // ── Task 3.3: DefaultParameterValueBinder state ───────────────────────────────

        /// <summary>
        /// Aliases of the current cmdlet, cached per invocation for <c>$PSDefaultParameterValues</c> matching.
        /// Previously stored as <c>_aliasList</c> on <see cref="DefaultParameterValueBinder"/>.
        /// </summary>
        internal List<string>? DefaultParameterAliasList { get; set; }

        /// <summary>
        /// Keys for which a <c>$PSDefaultParameterValues</c> warning has already been emitted.
        /// Prevents duplicate warnings within a single invocation.
        /// Previously stored as <c>_warningSet</c> on <see cref="DefaultParameterValueBinder"/>.
        /// </summary>
        internal HashSet<string> DefaultParameterWarningSet { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The last computed set of qualified default parameter value pairs.
        /// Previously stored as <c>_allDefaultParameterValuePairs</c> on <see cref="DefaultParameterValueBinder"/>.
        /// </summary>
        internal Dictionary<MergedCompiledCommandParameter, object>? AllDefaultParameterValuePairs { get; set; }

        /// <summary>
        /// Whether <c>$PSDefaultParameterValues</c> binding should be attempted for this invocation.
        /// Set to <see langword="false"/> when the dictionary has <c>Disabled=true</c>.
        /// Previously stored as <c>_useDefaultParameterBinding</c> on <see cref="DefaultParameterValueBinder"/>.
        /// </summary>
        internal bool UseDefaultParameterBinding { get; set; } = true;

        // ── Task 3.4: CmdletParameterBinderController obsolete-tracking state ─────────

        /// <summary>
        /// Names of parameters for which an obsolete warning has already been generated.
        /// Lazy-allocated; <see langword="null"/> when no obsolete parameters have been bound.
        /// Previously stored as <c>_boundObsoleteParameterNames</c> on <see cref="CmdletParameterBinderController"/>.
        /// </summary>
        internal HashSet<string>? BoundObsoleteParameterNames { get; set; }

        /// <summary>
        /// Accumulated obsolete-parameter warning records, flushed in <c>DoBegin</c>.
        /// Lazy-allocated; <see langword="null"/> when no obsolete parameters have been bound.
        /// Previously stored as <c>ObsoleteParameterWarningList</c> on <see cref="CmdletParameterBinderController"/>.
        /// </summary>
        internal List<WarningRecord>? ObsoleteParameterWarningList { get; set; }

        // ── Debugger display ──────────────────────────────────────────────────────────

        private string DebuggerDisplayValue
        {
            get
            {
                string name = CommandName ?? "(unknown)";
                int bound = BoundParameters.Count;
                int total = bound + UnboundParameters.Count;
                int args = UnboundArguments.Count;
                int pipeline = ParametersBoundThroughPipelineInput.Count;
                int defaults = BoundDefaultParameters.Count;
                int defVals = DefaultParameterValues.Count;
                int delayBind = DelayBindScriptBlocks.Count;
                string psDefaults = UseDefaultParameterBinding ? "InUse" : "Disabled";
                int obsolete = BoundObsoleteParameterNames?.Count ?? 0;
                return $"BindingState: {name}, Bound={bound}/{total}, Args={args}, Pipeline={pipeline}, Defaults={defaults}, DefVals={defVals}, DelayBind={delayBind}, PSDefaults={psDefaults}, Obsolete={obsolete}";
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resets all mutable state so the object is ready for reuse in a new command invocation.
        /// Collections are cleared (retaining their allocated capacity) and then <paramref name="allParameters"/>
        /// is repopulated into <see cref="UnboundParameters"/>.
        /// </summary>
        /// <param name="allParameters">The full set of parameters for the new invocation.</param>
        /// <param name="commandName">The name of the command being bound.</param>
        internal void Reset(IReadOnlyList<MergedCompiledCommandParameter> allParameters, string? commandName)
        {
            CommandName = commandName;

            // Argument tracking
            UnboundArguments.Clear();
            BoundArguments.Clear();

            // Parameter tracking
            UnboundParameters.Clear();
            UnboundParameters.AddRange(allParameters);
            BoundParameters.Clear();
            ParametersBoundThroughPipelineInput.Clear();

            // Default parameter tracking
            BoundDefaultParameters.Clear();
            DefaultParameterBindingInUse = false;

            // DefaultValueManager state (Task 3.1)
            DefaultParameterValues.Clear();

            // DelayBindScriptBlockHandler state (Task 3.2)
            DelayBindScriptBlocks.Clear();

            // DefaultParameterValueBinder state (Task 3.3)
            DefaultParameterAliasList = null;
            DefaultParameterWarningSet.Clear();
            AllDefaultParameterValuePairs = null;
            UseDefaultParameterBinding = true;

            // Obsolete-tracking state (Task 3.4)
            BoundObsoleteParameterNames = null;
            ObsoleteParameterWarningList = null;
        }

        /// <summary>
        /// Asserts that all collections are in their expected post-<see cref="Reset"/> state.
        /// Called in DEBUG builds after renting from the pool to detect stale state leaks.
        /// </summary>
        [Conditional("DEBUG")]
        internal void AssertClean(int expectedUnboundCount, [CallerMemberName] string caller = "")
        {
            Debug.Assert(BoundParameters.Count == 0, $"[{caller}] BoundParameters not clean after Reset");
            Debug.Assert(BoundArguments.Count == 0, $"[{caller}] BoundArguments not clean after Reset");
            Debug.Assert(UnboundArguments.Count == 0, $"[{caller}] UnboundArguments not clean after Reset");
            Debug.Assert(ParametersBoundThroughPipelineInput.Count == 0, $"[{caller}] PipelineInput not clean after Reset");
            Debug.Assert(BoundDefaultParameters.Count == 0, $"[{caller}] BoundDefaultParameters not clean after Reset");
            Debug.Assert(!DefaultParameterBindingInUse, $"[{caller}] DefaultParameterBindingInUse not clean after Reset");
            Debug.Assert(UnboundParameters.Count == expectedUnboundCount, $"[{caller}] UnboundParameters count mismatch after Reset: expected {expectedUnboundCount}, got {UnboundParameters.Count}");
            Debug.Assert(DefaultParameterValues.Count == 0, $"[{caller}] DefaultParameterValues not clean after Reset");
            Debug.Assert(DelayBindScriptBlocks.Count == 0, $"[{caller}] DelayBindScriptBlocks not clean after Reset");
            Debug.Assert(DefaultParameterAliasList == null, $"[{caller}] DefaultParameterAliasList not clean after Reset");
            Debug.Assert(DefaultParameterWarningSet.Count == 0, $"[{caller}] DefaultParameterWarningSet not clean after Reset");
            Debug.Assert(AllDefaultParameterValuePairs == null, $"[{caller}] AllDefaultParameterValuePairs not clean after Reset");
            Debug.Assert(UseDefaultParameterBinding, $"[{caller}] UseDefaultParameterBinding not reset to true after Reset");
            Debug.Assert(BoundObsoleteParameterNames == null, $"[{caller}] BoundObsoleteParameterNames not clean after Reset");
            Debug.Assert(ObsoleteParameterWarningList == null, $"[{caller}] ObsoleteParameterWarningList not clean after Reset");
        }
    }
}
