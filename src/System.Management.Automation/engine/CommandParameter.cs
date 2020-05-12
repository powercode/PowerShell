// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Management.Automation.Language;

namespace System.Management.Automation
{
    /// <summary>
    /// Represents a parameter to the Command.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    internal sealed class CommandParameterInternal
    {
        private class Parameter
        {
            internal Ast? Ast { get;  }
            internal string? Name { get; set; }
            internal string Text { get; }

            public Parameter(Ast? ast, string? name, string text)
            {
                Ast = ast;
                Name = name;
                Text = text;
            }

            public override string ToString()
            {
                return Name ?? Text;
            }
        }

        private class Argument
        {
            internal Ast? Ast { get; }
            internal object? Value { get; }
            internal bool Splatted { get; }

            public Argument(Ast? ast, object? value, bool splatted = false)
            {
                Ast = ast;
                Value = value;
                Splatted = splatted;
            }

            public override string ToString() => $"{Value}";
        }

        private Parameter? _parameter;
        private Argument? _argument;
        private bool _spaceAfterParameter;

        internal bool SpaceAfterParameter { get { return _spaceAfterParameter; } }

        internal bool ParameterNameSpecified { get { return _parameter != null; } }

        internal bool ArgumentSpecified { get { return _argument != null; } }

        internal bool ParameterAndArgumentSpecified { get { return ParameterNameSpecified && ArgumentSpecified; } }

        string DebuggerDisplay
        {
            get
            {
                return _parameter != null && _argument != null
                    ? $"{_parameter}: {_argument}"
                    : _parameter != null
                        ? $"{_parameter}"
                        : $"{_argument}";
            }
        }

        /// <summary>
        /// Gets and sets the string that represents parameter name, which does not include the '-' (dash).
        /// </summary>
        [DisallowNull]
        internal string ParameterName
        {
            get
            {
                Diagnostics.Assert(ParameterNameSpecified, "Caller must verify parameter name was specified");
                return _parameter?.Name ?? throw new InvalidOperationException("ParameterName must be specified");
            }

            set
            {
                Diagnostics.Assert(ParameterNameSpecified, "Caller must verify parameter name was specified");
                if (_parameter == null) throw new InvalidOperationException("Parameter must be specified");
                _parameter.Name = value;
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
                return _parameter?.Text ?? throw new InvalidOperationException("ParameterName must be specified");
            }
        }

        /// <summary>
        /// The ast of the parameter, if one was specified.
        /// </summary>
        internal Ast? ParameterAst
        {
            get => _parameter?.Ast;
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
        internal Ast? ArgumentAst
        {
            get => _argument?.Ast;
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
        internal object? ArgumentValue
        {
            get { return _argument != null ? _argument.Value : UnboundParameter.Value; }
        }

        /// <summary>
        /// If an argument was specified and is to be splatted, returns true, otherwise false.
        /// </summary>
        internal bool ArgumentSplatted
        {
            get { return _argument?.Splatted ?? false; }
        }

        /// <summary>
        /// Set the argument value and ast.
        /// </summary>
        internal void SetArgumentValue(Ast? ast, object? value)
        {
            _argument ??= new Argument(ast, value, false);
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
            Ast? ast = null)
        {
            return new CommandParameterInternal
            {
                _parameter = new Parameter(ast, parameterName, parameterText)
            };
        }

        /// <summary>
        /// Create a positional argument to a command.
        /// </summary>
        /// <param name="value">The argument value.</param>
        /// <param name="ast">The ast of the argument value in the script.</param>
        /// <param name="splatted">True if the argument value is to be splatted, false otherwise.</param>
        internal static CommandParameterInternal CreateArgument(
            object? value,
            Ast? ast = null,
            bool splatted = false)
        {
            return new CommandParameterInternal
            {
                _argument = new Argument(ast, value, splatted)
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
        internal static CommandParameterInternal CreateParameterWithArgument(
            Ast? parameterAst,
            string parameterName,
            string parameterText,
            Ast? argumentAst,
            object? value,
            bool spaceAfterParameter)
        {
            return new CommandParameterInternal
            {
                _parameter = new Parameter(parameterAst, parameterName, parameterText),
                _argument = new Argument(argumentAst, value),
                _spaceAfterParameter = spaceAfterParameter
            };
        }

        #endregion ctor

        internal bool IsDashQuestion()
        {
            return ParameterNameSpecified && (ParameterName!.Equals("?", StringComparison.OrdinalIgnoreCase));
        }
    }
}
