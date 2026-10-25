// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

namespace System.Management.Automation;

/// <summary>
/// Records the resolved pipeline binding plan after the first pipeline object is processed.
/// Subsequent objects in a homogeneous pipeline replay the plan directly, skipping the
/// 4-phase state machine, parameter-set filtering, and <c>ValidateParameterSets</c> calls.
/// </summary>
internal struct PipelineBindingPlan
{
    /// <summary>
    /// A single entry in the pipeline binding plan, describing one parameter
    /// that was bound through pipeline input.
    /// </summary>
    internal struct Entry
    {
        /// <summary>The resolved parameter to bind.</summary>
        internal MergedCompiledCommandParameter Parameter;

        /// <summary>
        /// <see langword="true"/> = ValueFromPipeline (whole object);
        /// <see langword="false"/> = ValueFromPipelineByPropertyName.
        /// </summary>
        internal bool IsValueFromPipeline;

        /// <summary>Coercion flags that were used when the plan was established.</summary>
        internal ParameterBindingFlags Flags;
    }

    /// <summary>Fixed-size array of plan entries. Sized at creation.</summary>
    internal Entry[] Entries;

    /// <summary>Number of valid entries in <see cref="Entries"/>.</summary>
    internal int Count;

    /// <summary>
    /// The resolved <c>CurrentParameterSetFlag</c> after the first pipeline object.
    /// Applied directly on subsequent objects instead of re-running <c>ValidateParameterSets</c>.
    /// </summary>
    internal uint ResolvedParameterSetFlag;

    /// <summary>
    /// Whether <c>ApplyDefaultParameterBinding</c> was applied on the first object.
    /// If <see langword="true"/>, the check is skipped on subsequent objects.
    /// </summary>
    internal bool DefaultParameterBindingApplied;

    /// <summary>
    /// Whether the plan contains any <c>ValueFromPipelineByPropertyName</c> entries.
    /// When <see langword="true"/>, the fast path must validate that the input object type
    /// has not changed since the plan was established.
    /// </summary>
    internal bool HasByPropertyName;

    /// <summary>
    /// The PS type name (first <c>InternalTypeNames</c> entry, or <c>BaseObject.GetType().FullName</c>)
    /// of the first input object. Used for type-guard validation when <see cref="HasByPropertyName"/> is <see langword="true"/>.
    /// </summary>
    internal string? FirstObjectTypeName;
}
