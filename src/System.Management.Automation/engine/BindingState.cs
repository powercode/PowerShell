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
                return $"BindingState: {name}, Bound={bound}/{total}, Args={args} unbound, Pipeline={pipeline}, Defaults={defaults}, DelayBind=n/a";
            }
        }

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
        }
    }
}
