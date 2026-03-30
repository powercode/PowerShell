// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace System.Management.Automation;

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

    private readonly IBindingStateContext _stateContext;
    private readonly IBindingOperationsContext _opsContext;
    private ParameterBinderBase? _dynamicParameterBinder;

    internal DynamicParameterHandler(IBindingStateContext stateContext, IBindingOperationsContext opsContext)
    {
        _stateContext = stateContext;
        _opsContext = opsContext;
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

        if (!_stateContext.ImplementsDynamicParameters)
        {
            return;
        }

        var command = _stateContext.Command;
        var commandLineParameters = _stateContext.CommandLineParameters;
        var bindableParameters = _stateContext.BindableParameters;
        var unboundArguments = _stateContext.UnboundArguments;

        using (ParameterBinderBase.bindingTracer.TraceScope("BIND cmd line args to DYNAMIC parameters."))
        {
            s_tracer.WriteLine("The Cmdlet supports the dynamic parameter interface");

            if (command is IDynamicParameters dynamicParameterCmdlet)
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
                            ParameterBindingException.NewGetDynamicParametersException(e, _stateContext.InvocationInfo, e.Message);

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
                                new RuntimeDefinedParameterBinder(runtimeParamDictionary, command, commandLineParameters);
                        }
                        else
                        {
                            // Generate the type metadata or retrieve it from the cache
                            dynamicParameterMetadata =
                                InternalParameterMetadata.Get(dynamicParamBindableObject.GetType(), _stateContext.Context, true);

                            // Create the parameter binder for the dynamic parameter object
                            _dynamicParameterBinder =
                                new ReflectionParameterBinder(dynamicParamBindableObject, command, commandLineParameters);
                        }

                        // Now merge the metadata with other metadata for the command
                        var dynamicParams =
                            bindableParameters.AddMetadataForBinder(dynamicParameterMetadata, ParameterBinderAssociation.DynamicParameters);
                        foreach (var param in dynamicParams)
                        {
                            _stateContext.UnboundParameters.Add(param);
                        }

                        // Now set the parameter set flags for the new type metadata.
                        _stateContext.DefaultParameterSetFlag =
                            bindableParameters.GenerateParameterSetMappingFromMetadata(_stateContext.DefaultParameterSetName);
                    }
                }

                if (_dynamicParameterBinder == null)
                {
                    s_tracer.WriteLine("No dynamic parameter object was returned from the Cmdlet");
                    return;
                }

                if (unboundArguments.Count > 0)
                {
                    using (ParameterBinderBase.bindingTracer.TraceScope("BIND NAMED args to DYNAMIC parameters"))
                    {
                        // Try to bind the unbound arguments as static parameters to the
                        // dynamic parameter object.

                        _opsContext.ReparseUnboundArguments();

                        _opsContext.BindNamedParameters(_stateContext.CurrentParameterSetFlag, unboundArguments);
                    }

                    using (ParameterBinderBase.bindingTracer.TraceScope("BIND POSITIONAL args to DYNAMIC parameters"))
                    {
                        _opsContext.BindPositionalParameters(
                            unboundArguments,
                            _stateContext.CurrentParameterSetFlag,
                            _stateContext.DefaultParameterSetFlag,
                            out outgoingBindingException);
                    }
                }
            }
        }
    }
}
