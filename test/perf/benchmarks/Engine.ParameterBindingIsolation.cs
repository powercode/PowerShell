// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using BenchmarkDotNet.Attributes;
using MicroBenchmarks;

#if SOURCE_BUILD
using System.Collections.Generic;
#endif

namespace Engine
{
#if SOURCE_BUILD
    /// <summary>
    /// Minimal test cmdlet used by the source-only isolation benchmarks.
    /// Provides 3 primary parameters (Name, Count, Enabled) plus 22 additional
    /// string parameters (P04–P25) for the large-parameter-count scenario.
    /// Only compiled when building against the PowerShell source tree
    /// (<c>SOURCE_BUILD</c> defined in powershell-perf.csproj).
    /// </summary>
    [Cmdlet("Test", "IsolatedBinding")]
    public sealed class TestIsolatedBindingCommand : Cmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Name { get; set; }

        [Parameter(Position = 1)]
        public int Count { get; set; }

        [Parameter]
        public SwitchParameter Enabled { get; set; }

        [Parameter(Position = 2)]
        public string Tag { get; set; }

        [Parameter]
        public string P04 { get; set; }

        [Parameter]
        public string P05 { get; set; }

        [Parameter]
        public string P06 { get; set; }

        [Parameter]
        public string P07 { get; set; }

        [Parameter]
        public string P08 { get; set; }

        [Parameter]
        public string P09 { get; set; }

        [Parameter]
        public string P10 { get; set; }

        [Parameter]
        public string P11 { get; set; }

        [Parameter]
        public string P12 { get; set; }

        [Parameter]
        public string P13 { get; set; }

        [Parameter]
        public string P14 { get; set; }

        [Parameter]
        public string P15 { get; set; }

        [Parameter]
        public string P16 { get; set; }

        [Parameter]
        public string P17 { get; set; }

        [Parameter]
        public string P18 { get; set; }

        [Parameter]
        public string P19 { get; set; }

        [Parameter]
        public string P20 { get; set; }

        [Parameter]
        public string P21 { get; set; }

        [Parameter]
        public string P22 { get; set; }

        [Parameter]
        public string P23 { get; set; }

        [Parameter]
        public string P24 { get; set; }

        [Parameter]
        public string P25 { get; set; }

        /// <inheritdoc/>
        protected override void ProcessRecord() { }
    }

    /// <summary>
    /// Cmdlet with a single <c>ValueFromPipeline</c> parameter for high-volume
    /// pipeline binding isolation benchmarks.
    /// </summary>
    [Cmdlet("Test", "IsolatedPipeline")]
    public sealed class TestIsolatedPipelineCommand : Cmdlet
    {
        [Parameter(ValueFromPipeline = true)]
        public int Value { get; set; }

        /// <inheritdoc/>
        protected override void ProcessRecord() { }
    }

    /// <summary>
    /// Cmdlet with two <c>ValueFromPipelineByPropertyName</c> parameters for
    /// property-name pipeline binding isolation benchmarks.
    /// </summary>
    [Cmdlet("Test", "IsolatedPipelineByPropertyName")]
    public sealed class TestIsolatedPipelineByPropertyNameCommand : Cmdlet
    {
        [Parameter(ValueFromPipelineByPropertyName = true)]
        public string Name { get; set; }

        [Parameter(ValueFromPipelineByPropertyName = true)]
        public int Count { get; set; }

        /// <inheritdoc/>
        protected override void ProcessRecord() { }
    }
#endif

    /// <summary>
    /// Isolation micro-benchmarks that target pure parameter-binding overhead in two ways:
    ///
    /// <para>
    /// <b>Source-build benchmarks</b> (<c>#if SOURCE_BUILD</c>): invoke
    /// <c>BindCommandLineParametersNoValidation()</c> directly on a
    /// <see cref="CmdletParameterBinderController"/> to measure pure binding cost without
    /// <c>ScriptBlock.Invoke()</c>, pipeline, or command-discovery noise. These use internal
    /// SMA APIs (<see cref="BindingState"/>, <see cref="ParameterSetResolver"/>, etc.) that
    /// exist only in the source tree; they are compiled only when <c>SOURCE_BUILD</c> is defined.
    /// </para>
    ///
    /// <para>
    /// <b>End-to-end comparison benchmark</b> (<c>PurePipelineBinding_SingleObject</c>):
    /// uses only public APIs and compiles against both the source tree and the PS 7.6 NuGet
    /// SDK, providing a per-object pipeline cost comparable with 7.6 baseline numbers.
    /// </para>
    /// </summary>
    [BenchmarkCategory(Categories.Engine, Categories.Internal)]
    public class ParameterBindingIsolation
    {
        // ── Infrastructure (all builds) ──────────────────────────────────────────

        private Runspace _runspace;
        private ScriptBlock _pipelineSingleObjectScript;

#if SOURCE_BUILD
        // ── Infrastructure (source build only) ──────────────────────────────────

        private CmdletParameterBinderController _controller;

        /// <summary>Snapshot of all bindable parameters; used to reset BindingState between iterations.</summary>
        private List<MergedCompiledCommandParameter> _allParams;

        // ── Pre-built argument collections (avoids measuring arg-construction cost) ──

        private Collection<CommandParameterInternal> _namedArgs3;
        private Collection<CommandParameterInternal> _positionalArgs3;
        private Collection<CommandParameterInternal> _namedArgs25;

        // ── High-volume pipeline binding infrastructure ─────────────────────────

        private CmdletParameterBinderController _pipelineController;
        private List<MergedCompiledCommandParameter> _pipelineAllParams;
        private PSObject[] _pipeline100kInts;

        private CmdletParameterBinderController _pipelineByPropNameController;
        private List<MergedCompiledCommandParameter> _pipelineByPropNameAllParams;
        private PSObject[] _pipeline100kObjects;
#endif

        // ── Setup / Teardown ─────────────────────────────────────────────────────

        /// <summary>One-time setup: open a runspace and pre-build all reusable state.</summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            // Open a default runspace — required by both the source-build controller
            // setup and the pipeline ScriptBlock benchmark.
            _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            _runspace.Open();
            Runspace.DefaultRunspace = _runspace;

#if SOURCE_BUILD
            // ── Source-build: obtain a CmdletParameterBinderController for direct binding ──

            // ExecutionContext is accessible from the LocalRunspace (internal API).
            var execContext = ((RunspaceBase)_runspace).GetExecutionContext;

            // CommandProcessor initialises the Cmdlet instance + MshCommandRuntime and
            // exposes a lazily-constructed CmdletParameterBinderController via its property.
            var cmdletInfo = new CmdletInfo("Test-IsolatedBinding", typeof(TestIsolatedBindingCommand));
            var processor = new CommandProcessor(cmdletInfo, execContext);
            _controller = processor.CmdletParameterBinderController;

            // Snapshot the full parameter list for BindingState.Reset() between iterations.
            _allParams = new List<MergedCompiledCommandParameter>(
                _controller.BindableParameters.BindableParameters.Values);

            // ── Named 3-param argument list ──────────────────────────────────────

            _namedArgs3 = new Collection<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameterWithArgument(
                    parameterAst: null, parameterName: "Name", parameterText: "-Name:",
                    argumentAst: null, value: "item", spaceAfterParameter: true),
                CommandParameterInternal.CreateParameterWithArgument(
                    parameterAst: null, parameterName: "Count", parameterText: "-Count:",
                    argumentAst: null, value: 12, spaceAfterParameter: true),
                CommandParameterInternal.CreateParameter(
                    parameterName: "Enabled", parameterText: "-Enabled"),
            };

            // ── Positional 3-param argument list ─────────────────────────────────

            _positionalArgs3 = new Collection<CommandParameterInternal>
            {
                CommandParameterInternal.CreateArgument(value: "item"),
                CommandParameterInternal.CreateArgument(value: 12),
                CommandParameterInternal.CreateArgument(value: "tag-value"),
            };

            // ── Named 25-param argument list ─────────────────────────────────────

            _namedArgs25 = new Collection<CommandParameterInternal>
            {
                CommandParameterInternal.CreateParameterWithArgument(
                    null, "Name", "-Name:", null, "item", true),
                CommandParameterInternal.CreateParameterWithArgument(
                    null, "Count", "-Count:", null, 12, true),
                CommandParameterInternal.CreateParameter("Enabled", "-Enabled"),
            };

            for (int i = 4; i <= 25; i++)
            {
                string pName = string.Format("P{0:D2}", i);
                string pText = string.Format("-{0}:", pName);
                string pValue = ((char)('a' + (i - 4))).ToString();
                _namedArgs25.Add(CommandParameterInternal.CreateParameterWithArgument(
                    null, pName, pText, null, pValue, true));
            }

            // ── High-volume pipeline binding: ValueFromPipeline ──────────────────

            {
                var pipelineCmdletInfo = new CmdletInfo("Test-IsolatedPipeline", typeof(TestIsolatedPipelineCommand));
                var pipelineProcessor = new CommandProcessor(pipelineCmdletInfo, execContext);
                _pipelineController = pipelineProcessor.CmdletParameterBinderController;
                _pipelineAllParams = new List<MergedCompiledCommandParameter>(
                    _pipelineController.BindableParameters.BindableParameters.Values);

                // Perform command-line binding with no arguments and pipeline expected.
                // The MshCommandRuntime.IsClosed defaults to false, so BindCommandLineParameters
                // treats this as a pipeline-expecting invocation and sets PrePipelineProcessingParameterSetFlags.
                _pipelineController.BindCommandLineParameters(new Collection<CommandParameterInternal>());

                // Pre-build 100k PSObject-wrapped ints for the pipeline loop.
                _pipeline100kInts = new PSObject[100_000];
                for (int i = 0; i < _pipeline100kInts.Length; i++)
                {
                    _pipeline100kInts[i] = PSObject.AsPSObject(i);
                }
            }

            // ── High-volume pipeline binding: ValueFromPipelineByPropertyName ────

            {
                var propNameCmdletInfo = new CmdletInfo("Test-IsolatedPipelineByPropertyName", typeof(TestIsolatedPipelineByPropertyNameCommand));
                var propNameProcessor = new CommandProcessor(propNameCmdletInfo, execContext);
                _pipelineByPropNameController = propNameProcessor.CmdletParameterBinderController;
                _pipelineByPropNameAllParams = new List<MergedCompiledCommandParameter>(
                    _pipelineByPropNameController.BindableParameters.BindableParameters.Values);

                _pipelineByPropNameController.BindCommandLineParameters(new Collection<CommandParameterInternal>());

                // Pre-build 100k PSObjects with Name + Count properties.
                _pipeline100kObjects = new PSObject[100_000];
                for (int i = 0; i < _pipeline100kObjects.Length; i++)
                {
                    var pso = new PSObject();
                    pso.Properties.Add(new PSNoteProperty("Name", "item"));
                    pso.Properties.Add(new PSNoteProperty("Count", i));
                    _pipeline100kObjects[i] = pso;
                }
            }
#endif

            // ── Pipeline single-object setup (all builds) ────────────────────────

            // Include the function definition inside the benchmark script block so the
            // function is always in scope when invoked. This follows the same pattern as
            // all other benchmarks in Engine.ParameterBinding.cs (e.g. multipleParameterSetBindingScript)
            // where the function definition and its call live in the same ScriptBlock.
            // A separate ScriptBlock.Create(...).Invoke() for the definition does NOT work:
            // the function is local to that script block's scope and is gone when it exits.
            _pipelineSingleObjectScript = ScriptBlock.Create(@"
function Test-IsolatedPipeline {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline = $true)]
        [string]$InputObject
    )
    process { }
}
'item' | Test-IsolatedPipeline");
        }

        /// <summary>One-time cleanup: dispose the runspace.</summary>
        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _runspace?.Dispose();
        }

#if SOURCE_BUILD
        // ── State reset helper (source build only) ───────────────────────────────

        /// <summary>
        /// Resets the <see cref="CmdletParameterBinderController"/> to its pre-bind state
        /// so every benchmark iteration starts clean without re-allocating objects.
        /// <see cref="BindingState.Reset"/> clears collections while retaining capacity;
        /// <see cref="ParameterSetResolver"/> flags are returned to their initial values.
        /// </summary>
        private void ResetController()
        {
            _controller.State.Reset(_allParams, "Test-IsolatedBinding");
            _controller.ParameterSetResolver.CurrentParameterSetFlag = uint.MaxValue;
            _controller.ParameterSetResolver.PrePipelineProcessingParameterSetFlags = uint.MaxValue;
            _controller.ParameterSetResolver.ParameterSetToBePrioritizedInPipelineBinding = 0;
        }

        // ── Source-build benchmarks ───────────────────────────────────────────────

        /// <summary>
        /// Pure named-parameter binding for 3 parameters: -Name, -Count, -Enabled.
        /// Calls <c>BindCommandLineParametersNoValidation()</c> directly, stripping away
        /// ScriptBlock/pipeline overhead to isolate exact binding allocations.
        /// Baseline comparison: <c>AdvancedFunctionDirectBinding</c> (~8.19 μs / 23.10 KB).
        /// </summary>
        [Benchmark]
        public int PureNamedBinding_3Params()
        {
            ResetController();
            _controller.BindCommandLineParametersNoValidation(_namedArgs3);
            return _controller.State.BoundParameters.Count;
        }

        /// <summary>
        /// Pure positional-parameter binding for 3 arguments (no parameter names).
        /// Exercises the <c>EvaluateUnboundPositionalParameters()</c> /
        /// <c>SortedDictionary</c> allocation path absent from the named benchmark.
        /// </summary>
        [Benchmark]
        public int PurePositionalBinding_3Params()
        {
            ResetController();
            _controller.BindCommandLineParametersNoValidation(_positionalArgs3);
            return _controller.State.BoundParameters.Count;
        }

        /// <summary>
        /// Pure named-parameter binding for all 25 parameters.
        /// Exposes the O(n²) <c>List.Remove()</c> cost in <c>UnboundParameters</c> and the
        /// wasted per-call <c>Collection&lt;&gt;</c> allocations in <c>GetMatchingParameterCore()</c>.
        /// Baseline comparison: <c>LargeParameterCountBinding</c> (~28.60 μs / 78.15 KB).
        /// </summary>
        [Benchmark]
        public int PureNamedBinding_25Params()
        {
            ResetController();
            _controller.BindCommandLineParametersNoValidation(_namedArgs25);
            return _controller.State.BoundParameters.Count;
        }

        // ── High-volume pipeline binding benchmarks (source build) ────────────────

        /// <summary>
        /// Binds 100,000 pipeline objects to a single <c>ValueFromPipeline</c> parameter.
        /// Calls <c>BindPipelineParameters()</c> directly on the controller, bypassing
        /// ScriptBlock/pipeline/command-discovery overhead. Each iteration measures
        /// pure per-object binding cost × 100 k, including <c>RestoreDefaultParameterValues</c>,
        /// 4-phase state machine, <c>ValidateParameterSets</c>, and value coercion.
        /// Baseline comparison: <c>HighVolumePipelineBinding</c> in Engine.ParameterBinding.
        /// </summary>
        [Benchmark]
        public int PurePipelineBinding_1Param_100kObjects()
        {
            var objects = _pipeline100kInts;
            int bound = 0;
            for (int i = 0; i < objects.Length; i++)
            {
                if (_pipelineController.BindPipelineParameters(objects[i]))
                {
                    bound++;
                }
            }

            return bound;
        }

        /// <summary>
        /// Binds 100,000 pipeline objects to two <c>ValueFromPipelineByPropertyName</c>
        /// parameters (Name, Count). Each object is a <see cref="PSObject"/> with matching
        /// NoteProperties. Measures per-object property-name lookup + binding cost × 100 k.
        /// Baseline comparison: <c>HighVolumePipelineByPropertyNameBinding</c>.
        /// </summary>
        [Benchmark]
        public int PurePipelineByPropName_2Params_100kObjects()
        {
            var objects = _pipeline100kObjects;
            int bound = 0;
            for (int i = 0; i < objects.Length; i++)
            {
                if (_pipelineByPropNameController.BindPipelineParameters(objects[i]))
                {
                    bound++;
                }
            }

            return bound;
        }
#endif

        // ── End-to-end comparison benchmark (all builds) ─────────────────────────

        /// <summary>
        /// Single-object pipeline binding through a minimal no-op advanced function.
        /// Uses only public <c>ScriptBlock.Invoke()</c> API — compiles against both the
        /// source tree and the PS 7.6 NuGet SDK for direct per-object cost comparison.
        /// Baseline: <c>PipelineParameterBinding</c> (100-obj mean) / 100 ≈ 0.75 μs.
        /// </summary>
        [Benchmark]
        public Collection<PSObject> PurePipelineBinding_SingleObject()
        {
            return _pipelineSingleObjectScript.Invoke();
        }
    }
}
