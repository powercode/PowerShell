// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Management.Automation.Internal;

namespace System.Management.Automation
{
    /// <summary>
    /// The parameter binder for runtime-defined parameters which are declared through the RuntimeDefinedParameterDictionary.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal class RuntimeDefinedParameterBinder : ParameterBinderBase
    {
        #region ctor

        /// <summary>
        /// Constructs the parameter binder with the specified type metadata. The binder is only valid
        /// for a single instance of a bindable runtime-defined parameter collection and only for the duration of a command.
        /// </summary>
        /// <param name="target">
        /// The target runtime-defined parameter collection that the parameter values will be bound to.
        /// </param>
        /// <param name="command">
        /// An instance of the command so that attributes can access the context.
        /// </param>
        /// <param name="commandLineParameters">
        /// The Command line parameter collection to update...
        /// </param>
        internal RuntimeDefinedParameterBinder(
            RuntimeDefinedParameterDictionary target,
            InternalCommand command,
            CommandLineParameters commandLineParameters)
            : base(target, command.MyInvocation, command.Context, command)
        {
            foreach (var pair in target)
            {
                string key = pair.Key;
                RuntimeDefinedParameter pp = pair.Value;
                string ppName = pp?.Name;
                if (pp == null || key != ppName)
                {
                    ParameterBindingException.ThrowRuntimeDefinedParameterNameMismatch(
                        command.MyInvocation,
                        ppName,
                        key);
                }
            }

            this.CommandLineParameters = commandLineParameters;
        }

        #endregion ctor

        #region internal members

        /// <summary>
        /// Hides the base class Target property to ensure the target
        /// is always a RuntimeDefinedParameterDictionary.
        /// </summary>
        internal new RuntimeDefinedParameterDictionary Target
        {
            get
            {
                return base.Target as RuntimeDefinedParameterDictionary;
            }

            set
            {
                base.Target = value;
            }
        }

        private string DebuggerDisplayValue
        {
            get
            {
                var target = Target;
                int count = target?.Count ?? 0;
                return $"RuntimeParamBinder: {count} params";
            }
        }

        #region Parameter default values

        /// <summary>
        /// Gets the default value for the specified parameter.
        /// </summary>
        /// <param name="name">
        /// The name of the parameter to get the value for.
        /// </param>
        /// <returns>
        /// The value of the specified parameter
        /// </returns>
        internal override object GetDefaultParameterValue(string name)
        {
            object result = null;
            RuntimeDefinedParameter parameter;
            if (this.Target.TryGetValue(name, out parameter) && parameter != null)
            {
                result = parameter.Value;
            }

            return result;
        }

        #endregion Parameter default values

        /// <summary>
        /// Uses ETS to set the property specified by name to the value on
        /// the target bindable object.
        /// </summary>
        /// <param name="name">
        ///     The name of the parameter to bind the value to.
        /// </param>
        /// <param name="value">
        ///     The value to bind to the parameter. It should be assumed by
        ///     derived classes that the proper type coercion has already taken
        ///     place and that any prerequisite metadata has been satisfied.
        /// </param>
        /// <param name="parameterMetadata"></param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="name"/> is null or empty.
        /// </exception>
        internal override void StoreParameterValue(string name, object value, CompiledCommandParameter parameterMetadata)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw PSTraceSource.NewArgumentException(nameof(name));
            }

            Target[name].Value = value;
            this.CommandLineParameters.Add(name, value);
        }

        #endregion Parameter binding
    }
}
