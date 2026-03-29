// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Management.Automation.Language;

namespace System.Management.Automation
{
    /// <summary>
    /// Represents a parameter to the Command.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplayValue,nq}")]
    internal sealed class CommandParameterInternal
    {
        private Ast _parameterAst;
        private string _parameterName;
        private string _parameterText;
        private bool _hasParameter;

        private Ast _argumentAst;
        private object _argumentValue;
        private bool _argumentSplatted;
        private bool _hasArgument;

        private bool _spaceAfterParameter;
        private bool _fromHashtableSplatting;

        internal bool SpaceAfterParameter => _spaceAfterParameter;

        internal bool ParameterNameSpecified => _hasParameter;

        internal bool ArgumentSpecified => _hasArgument;

        internal bool ParameterAndArgumentSpecified => ParameterNameSpecified && ArgumentSpecified;

        internal bool FromHashtableSplatting => _fromHashtableSplatting;

        /// <summary>
        /// Gets and sets the string that represents parameter name, which does not include the '-' (dash).
        /// </summary>
        internal string ParameterName
        {
            get
            {
                Diagnostics.Assert(ParameterNameSpecified, "Caller must verify parameter name was specified");
                return _parameterName;
            }

            set
            {
                Diagnostics.Assert(ParameterNameSpecified, "Caller must verify parameter name was specified");
                _parameterName = value;
            }
        }

        /// <summary>
        /// The text of the parameter, which typically includes the leading '-' (dash) and, if specified, the trailing ':'.
        /// </summary>
        internal string ParameterText
        {
            get
            {
                Diagnostics.Assert(ParameterNameSpecified, "Caller must verify parameter name was specified");
                return _parameterText;
            }
        }

        /// <summary>
        /// The ast of the parameter, if one was specified.
        /// </summary>
        internal Ast ParameterAst
        {
            get => _hasParameter ? _parameterAst : null;
        }

        /// <summary>
        /// The extent of the parameter, if one was specified.
        /// </summary>
        internal IScriptExtent ParameterExtent
        {
            get => ParameterAst?.Extent ?? PositionUtilities.EmptyExtent;
        }

        /// <summary>
        /// The ast of the optional argument, if one was specified.
        /// </summary>
        internal Ast ArgumentAst
        {
            get => _hasArgument ? _argumentAst : null;
        }

        /// <summary>
        /// The extent of the optional argument, if one was specified.
        /// </summary>
        internal IScriptExtent ArgumentExtent
        {
            get => ArgumentAst?.Extent ?? PositionUtilities.EmptyExtent;
        }

        /// <summary>
        /// The value of the optional argument, if one was specified, otherwise UnboundParameter.Value.
        /// </summary>
        internal object ArgumentValue
        {
            get { return _hasArgument ? _argumentValue : UnboundParameter.Value; }
        }

        /// <summary>
        /// If an argument was specified and is to be splatted, returns true, otherwise false.
        /// </summary>
        internal bool ArgumentToBeSplatted
        {
            get { return _hasArgument && _argumentSplatted; }
        }

        /// <summary>
        /// Set the argument value and ast.
        /// </summary>
        internal void SetArgumentValue(Ast ast, object value)
        {
            _hasArgument = true;
            _argumentValue = value;
            _argumentAst = ast;
        }

        /// <summary>
        /// The extent to use when reporting generic errors.  The argument extent is used, if it is not empty, otherwise
        /// the parameter extent is used.  Some errors may prefer the parameter extent and should not use this method.
        /// </summary>
        internal IScriptExtent ErrorExtent
        {
            get
            {
                var argExtent = ArgumentExtent;
                return argExtent != PositionUtilities.EmptyExtent ? argExtent : ParameterExtent;
            }
        }

        private string DebuggerDisplayValue
        {
            get
            {
                if (ParameterNameSpecified && ArgumentSpecified)
                {
                    string val = ArgumentValue?.ToString() ?? "null";
                    if (val.Length > 50) val = val[..50] + "...";
                    return $"-{_parameterName}: {val}";
                }

                if (ParameterNameSpecified)
                    return $"-{_parameterName} (no arg)";

                if (ArgumentSpecified)
                {
                    string val = ArgumentValue?.ToString() ?? "null";
                    if (val.Length > 50) val = val[..50] + "...";
                    return $"(positional) {val}";
                }

                return "(empty)";
            }
        }

        #region ctor

        /// <summary>
        /// Create a parameter when no argument has been specified.
        /// </summary>
        /// <param name="ast">The ast in script of the parameter.</param>
        /// <param name="parameterName">The parameter name (with no leading dash).</param>
        /// <param name="parameterText">The text of the parameter, as it did, or would, appear in script.</param>
        internal static CommandParameterInternal CreateParameter(
            string parameterName,
            string parameterText,
            Ast ast = null)
        {
            return new CommandParameterInternal
            {
                _parameterAst = ast,
                _parameterName = parameterName,
                _parameterText = parameterText,
                _hasParameter = true,
            };
        }

        /// <summary>
        /// Create a positional argument to a command.
        /// </summary>
        /// <param name="value">The argument value.</param>
        /// <param name="ast">The ast of the argument value in the script.</param>
        /// <param name="splatted">True if the argument value is to be splatted, false otherwise.</param>
        internal static CommandParameterInternal CreateArgument(
            object value,
            Ast ast = null,
            bool splatted = false)
        {
            return new CommandParameterInternal
            {
                _argumentAst = ast,
                _argumentValue = value,
                _argumentSplatted = splatted,
                _hasArgument = true,
            };
        }

        /// <summary>
        /// Create an named argument, where the parameter name is known.  This can happen when:
        ///     * The user uses the ':' syntax, as in
        ///         foo -bar:val
        ///     * Splatting, as in
        ///         $x = @{ bar = val } ; foo @x
        ///     * Via an API - when converting a CommandParameter to CommandParameterInternal.
        ///     * In the parameter binder when it resolves a positional argument
        ///     * Other random places that manually construct command processors and know their arguments.
        /// </summary>
        /// <param name="parameterAst">The ast in script of the parameter.</param>
        /// <param name="parameterName">The parameter name (with no leading dash).</param>
        /// <param name="parameterText">The text of the parameter, as it did, or would, appear in script.</param>
        /// <param name="argumentAst">The ast of the argument value in the script.</param>
        /// <param name="value">The argument value.</param>
        /// <param name="spaceAfterParameter">Used in native commands to correctly handle -foo:bar vs. -foo: bar.</param>
        /// <param name="fromSplatting">Indicate if this parameter-argument pair comes from splatting.</param>
        internal static CommandParameterInternal CreateParameterWithArgument(
            Ast parameterAst,
            string parameterName,
            string parameterText,
            Ast argumentAst,
            object value,
            bool spaceAfterParameter,
            bool fromSplatting = false)
        {
            return new CommandParameterInternal
            {
                _parameterAst = parameterAst,
                _parameterName = parameterName,
                _parameterText = parameterText,
                _hasParameter = true,
                _argumentAst = argumentAst,
                _argumentValue = value,
                _hasArgument = true,
                _spaceAfterParameter = spaceAfterParameter,
                _fromHashtableSplatting = fromSplatting,
            };
        }

        #endregion ctor

        internal bool IsDashQuestion()
        {
            return ParameterNameSpecified && (ParameterName.Equals("?", StringComparison.OrdinalIgnoreCase));
        }
    }
}
