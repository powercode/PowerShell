// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Management.Automation
{
    /// <summary>
    /// Consolidates the per-invocation mutable binding state for the parameter binding subsystem.
    /// A single <see cref="ParameterBindingState"/> instance travels with each command invocation.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal sealed class ParameterBindingState
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
        internal List<CommandParameterInternal> UnboundArguments { get; set; } = [];

        /// <summary>
        /// Arguments that have been bound, keyed by parameter name (OrdinalIgnoreCase).
        /// </summary>
        internal Dictionary<string, CommandParameterInternal> BoundArguments { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Names of parameters that were bound via <c>$PSDefaultParameterValues</c>.
        /// </summary>
        internal List<string> BoundDefaultParameters { get; } = [];

        /// <summary>
        /// Whether default parameter binding from <c>$PSDefaultParameterValues</c> is active.
        /// </summary>
        internal bool DefaultParameterBindingInUse { get; set; }

        /// <summary>
        /// Parameters that received their value from the pipeline input.
        /// </summary>
        internal List<MergedCompiledCommandParameter> ParametersBoundThroughPipelineInput { get; } = [];

        // ── Default-value tracking ──────────────────────────────────────────────────

        /// <summary>
        /// Saved default values for restoration after each pipeline-object is processed,
        /// keyed by parameter name (OrdinalIgnoreCase).
        /// </summary>
        internal Dictionary<string, CommandParameterInternal> DefaultParameterValues { get; }
            = new Dictionary<string, CommandParameterInternal>(StringComparer.OrdinalIgnoreCase);

        // ── Delay-bind ScriptBlock state ──────────────────────────────────────────────

        /// <summary>
        /// ScriptBlock arguments deferred for per-pipeline-object evaluation, keyed by parameter.
        /// </summary>
        internal Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument> DelayBindScriptBlocks { get; }
            = new Dictionary<MergedCompiledCommandParameter, DelayBindScriptBlockHandler.DelayedScriptBlockArgument>();

        // ── $PSDefaultParameterValues state ──────────────────────────────────────────

        /// <summary>
        /// Aliases of the current cmdlet, cached per invocation for <c>$PSDefaultParameterValues</c> matching.
        /// </summary>
        internal List<string>? DefaultParameterAliasList { get; set; }

        /// <summary>
        /// Keys for which a <c>$PSDefaultParameterValues</c> warning has already been emitted.
        /// Prevents duplicate warnings within a single invocation.
        /// </summary>
        internal HashSet<string> DefaultParameterWarningSet { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The last computed set of qualified default parameter value pairs.
        /// </summary>
        internal Dictionary<MergedCompiledCommandParameter, object>? AllDefaultParameterValuePairs { get; set; }

        /// <summary>
        /// Whether <c>$PSDefaultParameterValues</c> binding should be attempted for this invocation.
        /// Set to <see langword="false"/> when the dictionary has <c>Disabled=true</c>.
        /// </summary>
        internal bool UseDefaultParameterBinding { get; set; } = true;

        // ── Obsolete-parameter tracking ────────────────────────────────────────────

        /// <summary>
        /// Names of parameters for which an obsolete warning has already been generated.
        /// Lazy-allocated; <see langword="null"/> when no obsolete parameters have been bound.
        /// </summary>
        internal HashSet<string>? BoundObsoleteParameterNames { get; set; }

        /// <summary>
        /// Accumulated obsolete-parameter warning records, flushed in <c>DoBegin</c>.
        /// Lazy-allocated; <see langword="null"/> when no obsolete parameters have been bound.
        /// </summary>
        internal List<WarningRecord>? ObsoleteParameterWarningList { get; set; }

        // ── Positional parameter dictionary cache ─────────────────────────────────────

        /// <summary>
        /// Cached result of <c>EvaluateUnboundPositionalParameters</c>.
        /// Valid when <see cref="_cachedPositionalUnboundCount"/> and
        /// <see cref="_cachedPositionalSetFlag"/> match the current binding state.
        /// </summary>
        private SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>? _cachedPositionalDict;
        private int _cachedPositionalUnboundCount;
        private uint _cachedPositionalSetFlag;

        /// <summary>
        /// Returns the cached positional dictionary if the (unboundCount, setFlag) key matches;
        /// otherwise returns <see langword="null"/>.
        /// </summary>
        internal SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>>?
            GetCachedPositionalDictionary(int unboundCount, uint validParameterSetFlag)
        {
            if (_cachedPositionalDict != null
                && _cachedPositionalUnboundCount == unboundCount
                && _cachedPositionalSetFlag == validParameterSetFlag)
            {
                return _cachedPositionalDict;
            }
            return null;
        }

        /// <summary>
        /// Stores the computed positional dictionary together with the key used to validate it.
        /// </summary>
        internal void SetCachedPositionalDictionary(
            SortedDictionary<int, Dictionary<MergedCompiledCommandParameter, PositionalCommandParameter>> dict,
            int unboundCount, uint validParameterSetFlag)
        {
            _cachedPositionalDict = dict;
            _cachedPositionalUnboundCount = unboundCount;
            _cachedPositionalSetFlag = validParameterSetFlag;
        }

        // ── Pipeline CPI pool ─────────────────────────────────────────────────────────

        /// <summary>
        /// Per-invocation CPI pool for the pipeline binding hot path.
        /// Sized for typical pipeline parameter counts (1-4); excess CPIs are GC'd normally.
        /// </summary>
        private CommandParameterInternal[]? _cpiPool;
        private int _cpiPoolCount;

        /// <summary>
        /// Rents a <see cref="CommandParameterInternal"/> from the pool, or allocates a new one.
        /// </summary>
        internal CommandParameterInternal RentPipelineCpi()
        {
            if (_cpiPoolCount > 0)
            {
                var cpi = _cpiPool![--_cpiPoolCount];
                _cpiPool[_cpiPoolCount] = null!; // don't hold refs
                return cpi;
            }

            return new CommandParameterInternal();
        }

        /// <summary>
        /// Returns a <see cref="CommandParameterInternal"/> to the pool after resetting its fields.
        /// CPIs beyond the pool capacity are simply dropped for GC.
        /// </summary>
        internal void ReturnPipelineCpi(CommandParameterInternal cpi)
        {
            cpi.Reset();
            _cpiPool ??= new CommandParameterInternal[4];
            if (_cpiPoolCount < _cpiPool.Length)
            {
                _cpiPool[_cpiPoolCount++] = cpi;
            }
        }

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

            // Default-value tracking
            DefaultParameterValues.Clear();

            // Delay-bind ScriptBlock state
            DelayBindScriptBlocks.Clear();

            // $PSDefaultParameterValues state
            DefaultParameterAliasList = null;
            DefaultParameterWarningSet.Clear();
            AllDefaultParameterValuePairs = null;
            UseDefaultParameterBinding = true;

            // Obsolete-parameter tracking
            BoundObsoleteParameterNames = null;
            ObsoleteParameterWarningList = null;

            // Positional parameter dictionary cache
            _cachedPositionalDict = null;
            _cachedPositionalUnboundCount = 0;
            _cachedPositionalSetFlag = 0;

            // Pipeline CPI pool — drop all refs so pooled CPIs don't outlive this invocation
            _cpiPoolCount = 0;
            _cpiPool = null;
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
            Debug.Assert(_cachedPositionalDict == null, $"[{caller}] _cachedPositionalDict not clean after Reset");
        }

        // ── O(1) list-removal helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Removes <paramref name="item"/> from <paramref name="list"/> in O(1) by swapping it
        /// with the last element and removing the tail. Order is NOT preserved.
        /// </summary>
        /// <returns><see langword="true"/> if the item was found and removed; otherwise <see langword="false"/>.</returns>
        internal static bool SwapRemove<T>(IList<T> list, T item)
        {
            int index = list.IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            int lastIndex = list.Count - 1;
            if (index < lastIndex)
            {
                list[index] = list[lastIndex];
            }

            list.RemoveAt(lastIndex);
            return true;
        }
    }
}
