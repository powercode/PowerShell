// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace System.Management.Automation;

/// <summary>
/// Provides the binding context that <see cref="DynamicParameterHandler"/> needs
/// from its owning parameter binder controller.
/// </summary>
internal interface IDynamicParameterHandlerContext
{
    /// <summary>Whether the command implements <see cref="IDynamicParameters"/>.</summary>
    bool ImplementsDynamicParameters { get; }

    /// <summary>The cmdlet instance being bound.</summary>
    Cmdlet Command { get; }

    /// <summary>The engine execution context.</summary>
    ExecutionContext Context { get; }

    /// <summary>Invocation info for the current command (used in exception messages).</summary>
    InvocationInfo InvocationInfo { get; }

    /// <summary>The merged parameter metadata including all sub-binders.</summary>
    MergedCommandParameterMetadata BindableParameters { get; }

    /// <summary>Parameters not yet bound to a value.</summary>
    IList<MergedCompiledCommandParameter> UnboundParameters { get; }

    /// <summary>Arguments not yet matched to a parameter.</summary>
    Collection<CommandParameterInternal> UnboundArguments { get; set; }

    /// <summary>Current valid parameter set flags.</summary>
    uint CurrentParameterSetFlag { get; }

    /// <summary>Default parameter set flag; writable so the handler can update it after merging dynamic parameter metadata.</summary>
    uint DefaultParameterSetFlag { get; set; }

    /// <summary>Name of the default parameter set.</summary>
    string DefaultParameterSetName { get; }

    /// <summary>Command-line parameter tracker shared across binders.</summary>
    CommandLineParameters CommandLineParameters { get; }

    /// <summary>Re-parses unbound arguments to pair parameter names with following values.</summary>
    void ReparseUnboundArguments();

    /// <summary>Binds named parameters from <paramref name="args"/> against the current parameter set.</summary>
    void BindNamedParameters(uint parameterSetFlag, Collection<CommandParameterInternal> args);

    /// <summary>Binds positional parameters from <paramref name="args"/>.</summary>
    void BindPositionalParameters(
        Collection<CommandParameterInternal> args,
        uint currentParameterSetFlag,
        uint defaultParameterSetFlag,
        out ParameterBindingException? outgoingBindingException);
}

/// <summary>
/// Handles dynamic parameter discovery, metadata merge, and re-bind phases for cmdlets
/// that implement <see cref="IDynamicParameters"/>.
/// </summary>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal sealed class DynamicParameterHandler
{
    [TraceSource("ParameterBinderController", "Controls the interaction between the command processor and the parameter binder(s).")]
    private static readonly PSTraceSource s_tracer =
        PSTraceSource.GetTracer(
            "ParameterBinderController",
            "Controls the interaction between the command processor and the parameter binder(s).");

    private readonly IDynamicParameterHandlerContext _context;
    private ParameterBinderBase? _dynamicParameterBinder;

    internal DynamicParameterHandler(IDynamicParameterHandlerContext context)
    {
        _context = context;
    }

    private string DebuggerDisplayValue =>  $"DynamicBinder: {(_dynamicParameterBinder != null ? _dynamicParameterBinder.GetType().Name : "none")}";

    /// <summary>The active binder for dynamic parameters, or <c>null</c> if none have been discovered yet.</summary>
    internal ParameterBinderBase? Binder => _dynamicParameterBinder;

    /// <summary>
    /// Discovers dynamic parameters, merges their metadata, and re-binds any unmatched
    /// command-line arguments against the newly available parameter definitions.
    /// </summary>
    internal void Handle(out ParameterBindingException? outgoingBindingException)
    {
        outgoingBindingException = null;

        if (!_context.ImplementsDynamicParameters)
        {
            return;
        }

        using (ParameterBinderBase.bindingTracer.TraceScope("BIND cmd line args to DYNAMIC parameters."))
        {
            s_tracer.WriteLine("The Cmdlet supports the dynamic parameter interface");

            if (_context.Command is IDynamicParameters dynamicParameterCmdlet)
            {
                if (_dynamicParameterBinder == null)
                {
                    s_tracer.WriteLine("Getting the bindable object from the Cmdlet");

                    // Now get the dynamic parameter bindable object.
                    object? dynamicParamBindableObject;

                    try
                    {
                        dynamicParamBindableObject = dynamicParameterCmdlet.GetDynamicParameters();
                    }
                    catch (Exception e) // Catch-all OK, this is a third-party callout
                    {
                        if (e is ProviderInvocationException)
                        {
                            throw;
                        }

                        ParameterBindingException bindingException =
                            ParameterBindingException.NewGetDynamicParametersException(
                                e,
                                _context.InvocationInfo,
                                e.Message);

                        // This exception is caused because failure happens when retrieving the dynamic parameters,
                        // this is not caused by introducing the default parameter binding.
                        throw bindingException;
                    }

                    if (dynamicParamBindableObject != null)
                    {
                        ParameterBinderBase.bindingTracer.WriteLine("DYNAMIC parameter object: [{0}]", dynamicParamBindableObject.GetType());

                        s_tracer.WriteLine("Creating a new parameter binder for the dynamic parameter object");

                        InternalParameterMetadata dynamicParameterMetadata;

                        if (dynamicParamBindableObject is RuntimeDefinedParameterDictionary runtimeParamDictionary)
                        {
                            // Generate the type metadata for the runtime-defined parameters
                            dynamicParameterMetadata = InternalParameterMetadata.Get(runtimeParamDictionary, true, true);

                            _dynamicParameterBinder =
                                new RuntimeDefinedParameterBinder(runtimeParamDictionary, _context.Command, _context.CommandLineParameters);
                        }
                        else
                        {
                            // Generate the type metadata or retrieve it from the cache
                            dynamicParameterMetadata =
                                InternalParameterMetadata.Get(dynamicParamBindableObject.GetType(), _context.Context, true);

                            // Create the parameter binder for the dynamic parameter object
                            _dynamicParameterBinder =
                                new ReflectionParameterBinder(dynamicParamBindableObject, _context.Command, _context.CommandLineParameters);
                        }

                        // Now merge the metadata with other metadata for the command
                        var dynamicParams =
                            _context.BindableParameters.AddMetadataForBinder(dynamicParameterMetadata, ParameterBinderAssociation.DynamicParameters);
                        foreach (var param in dynamicParams)
                        {
                            _context.UnboundParameters.Add(param);
                        }

                        // Now set the parameter set flags for the new type metadata.
                        _context.DefaultParameterSetFlag =
                            _context.BindableParameters.GenerateParameterSetMappingFromMetadata(_context.DefaultParameterSetName);
                    }
                }

                if (_dynamicParameterBinder == null)
                {
                    s_tracer.WriteLine("No dynamic parameter object was returned from the Cmdlet");
                    return;
                }

                if (_context.UnboundArguments.Count > 0)
                {
                    using (ParameterBinderBase.bindingTracer.TraceScope("BIND NAMED args to DYNAMIC parameters"))
                    {
                        // Try to bind the unbound arguments as static parameters to the
                        // dynamic parameter object.

                        _context.ReparseUnboundArguments();

                        _context.BindNamedParameters(_context.CurrentParameterSetFlag, _context.UnboundArguments);
                    }

                    using (ParameterBinderBase.bindingTracer.TraceScope("BIND POSITIONAL args to DYNAMIC parameters"))
                    {
                        _context.BindPositionalParameters(
                            _context.UnboundArguments,
                            _context.CurrentParameterSetFlag,
                            _context.DefaultParameterSetFlag,
                            out outgoingBindingException);
                    }
                }
            }
        }
    }
}
