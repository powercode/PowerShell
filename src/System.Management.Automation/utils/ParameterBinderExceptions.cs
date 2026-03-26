// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;

namespace System.Management.Automation
{
    /// <summary>
    /// The exception thrown if the specified value can not be bound parameter of a command.
    /// </summary>
    public class ParameterBindingException : RuntimeException
    {
        #region Constructors

        #region Preferred constructors

        /// <summary>
        /// Constructs a ParameterBindingException.
        /// </summary>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        /// <!--
        /// InvocationInfo.MyCommand.Name == {0}
        /// -->
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        /// If position is null, the one from the InvocationInfo is used.
        /// <!--
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// -->
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        /// <!--
        /// parameterName == {1}
        /// -->
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        /// <!--
        /// parameterType == {2}
        /// -->
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        /// <!--
        /// typeSpecified == {3}
        /// -->
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        /// <!--
        /// starts at {6}
        /// -->
        /// </param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingException(
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(errorCategory, invocationInfo, errorPosition, errorId, null, null)
        {
            if (string.IsNullOrEmpty(resourceString))
            {
                throw PSTraceSource.NewArgumentException(nameof(resourceString));
            }

            if (string.IsNullOrEmpty(errorId))
            {
                throw PSTraceSource.NewArgumentException(nameof(errorId));
            }

            _invocationInfo = invocationInfo;

            if (_invocationInfo != null)
            {
                _commandName = invocationInfo.MyCommand.Name;
            }

            _parameterName = parameterName;
            _parameterType = parameterType;
            _typeSpecified = typeSpecified;

            if ((errorPosition == null) && (_invocationInfo != null))
            {
                errorPosition = invocationInfo.ScriptPosition;
            }

            if (errorPosition != null)
            {
                _line = errorPosition.StartLineNumber;
                _offset = errorPosition.StartColumnNumber;
            }

            _resourceString = resourceString;
            _errorId = errorId;

            if (args != null)
            {
                _args = args;
            }
        }

        /// <summary>
        /// Constructs a ParameterBindingException.
        /// </summary>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        /// If position is null, the one from the InvocationInfo is used.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="invocationInfo"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingException(
            Exception innerException,
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(errorCategory, invocationInfo, errorPosition, errorId, null, innerException)
        {
            if (invocationInfo == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(invocationInfo));
            }

            if (string.IsNullOrEmpty(resourceString))
            {
                throw PSTraceSource.NewArgumentException(nameof(resourceString));
            }

            if (string.IsNullOrEmpty(errorId))
            {
                throw PSTraceSource.NewArgumentException(nameof(errorId));
            }

            _invocationInfo = invocationInfo;
            _commandName = invocationInfo.MyCommand.Name;
            _parameterName = parameterName;
            _parameterType = parameterType;
            _typeSpecified = typeSpecified;

            errorPosition ??= invocationInfo.ScriptPosition;

            if (errorPosition != null)
            {
                _line = errorPosition.StartLineNumber;
                _offset = errorPosition.StartColumnNumber;
            }

            _resourceString = resourceString;
            _errorId = errorId;

            if (args != null)
            {
                _args = args;
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="innerException"></param>
        /// <param name="pbex"></param>
        /// <param name="resourceString"></param>
        /// <param name="args"></param>
        internal ParameterBindingException(
            Exception innerException,
            ParameterBindingException pbex,
            string resourceString,
            params object[] args)
            : base(string.Empty, innerException)
        {
            if (pbex == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(pbex));
            }

            if (string.IsNullOrEmpty(resourceString))
            {
                throw PSTraceSource.NewArgumentException(nameof(resourceString));
            }

            _invocationInfo = pbex.CommandInvocation;
            if (_invocationInfo != null)
            {
                _commandName = _invocationInfo.MyCommand.Name;
            }

            IScriptExtent errorPosition = null;
            if (_invocationInfo != null)
            {
                errorPosition = _invocationInfo.ScriptPosition;
            }

            _line = pbex.Line;
            _offset = pbex.Offset;

            _parameterName = pbex.ParameterName;
            _parameterType = pbex.ParameterType;
            _typeSpecified = pbex.TypeSpecified;
            _errorId = pbex.ErrorId;

            _resourceString = resourceString;

            if (args != null)
            {
                _args = args;
            }

            base.SetErrorCategory(pbex.ErrorRecord._category);
            base.SetErrorId(_errorId);
            if (_invocationInfo != null)
            {
                base.ErrorRecord.SetInvocationInfo(new InvocationInfo(_invocationInfo.MyCommand, errorPosition));
            }
        }
        #endregion Preferred constructors

        #region serialization
        /// <summary>
        /// Constructors a ParameterBindingException using serialized data.
        /// </summary>
        /// <param name="info">
        /// serialization information
        /// </param>
        /// <param name="context">
        /// streaming context
        /// </param>
        [Obsolete("Legacy serialization support is deprecated since .NET 8", DiagnosticId = "SYSLIB0051")]
        protected ParameterBindingException(
            SerializationInfo info,
            StreamingContext context)
        {
            throw new NotSupportedException();
        }
        #endregion serialization

        #region Do Not Use

        /// <summary>
        /// Constructs a ParameterBindingException.
        /// </summary>
        /// <remarks>
        /// DO NOT USE!!!
        /// </remarks>
        public ParameterBindingException() : base() { }

        /// <summary>
        /// Constructors a ParameterBindingException.
        /// </summary>
        /// <param name="message">
        /// Message to be included in exception.
        /// </param>
        /// <remarks>
        /// DO NOT USE!!!
        /// </remarks>
        public ParameterBindingException(string message) : base(message) { _message = message; }

        /// <summary>
        /// Constructs a ParameterBindingException.
        /// </summary>
        /// <param name="message">
        /// Message to be included in the exception.
        /// </param>
        /// <param name="innerException">
        /// exception that led to this exception
        /// </param>
        /// <remarks>
        /// DO NOT USE!!!
        /// </remarks>
        public ParameterBindingException(
            string message,
            Exception innerException)
            : base(message, innerException)
        { _message = message; }

        #endregion Do Not Use
        #endregion Constructors

        #region ThrowHelpers

        [DoesNotReturn]
        internal static void ThrowParameterAlreadyBound(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                null,
                null,
                ParameterBinderStrings.ParameterAlreadyBound,
                nameof(ParameterBinderStrings.ParameterAlreadyBound));
        }

        [DoesNotReturn]
        internal static void ThrowNamedParameterNotFound(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type typeSpecified)
        {
            throw NewNamedParameterNotFound(invocationInfo, errorPosition, parameterName, typeSpecified);
        }

        [DoesNotReturn]
        internal static void ThrowMissingArgument(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                null,
                ParameterBinderStrings.MissingArgument,
                "MissingArgument");
        }

        [DoesNotReturn]
        internal static void ThrowCannotConvertArgument(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            object argumentValue,
            string errorMessage)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.CannotConvertArgument,
                "CannotConvertArgument",
                argumentValue ?? "null",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowCannotConvertArgumentNoMessage(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.CannotConvertArgumentNoMessage,
                "CannotConvertArgumentNoMessage",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowMissingMandatoryParameter(
            InvocationInfo invocationInfo,
            string missingParameters)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                missingParameters,
                null,
                null,
                ParameterBinderStrings.MissingMandatoryParameter,
                "MissingMandatoryParameter");
        }

        [DoesNotReturn]
        internal static void ThrowParameterBindingFailed(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.WriteError,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterBindingFailed,
                "ParameterBindingFailed",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowScriptBlockArgumentNoInput(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType)
        {
            throw new ParameterBindingException(
                ErrorCategory.MetadataError,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                null,
                ParameterBinderStrings.ScriptBlockArgumentNoInput,
                "ScriptBlockArgumentNoInput");
        }

        [DoesNotReturn]
        internal static void ThrowScriptBlockArgumentInvocationFailed(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ScriptBlockArgumentInvocationFailed,
                "ScriptBlockArgumentInvocationFailed",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowScriptBlockArgumentNoOutput(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                null,
                ParameterBinderStrings.ScriptBlockArgumentNoOutput,
                "ScriptBlockArgumentNoOutput");
        }

        [DoesNotReturn]
        internal static void ThrowCannotExtractAddMethod(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.CannotExtractAddMethod,
                "CannotExtractAddMethod",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowAmbiguousParameter(
            InvocationInfo invocationInfo,
            string parameterName,
            string matchingParameters)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                parameterName,
                null,
                null,
                ParameterBinderStrings.AmbiguousParameter,
                "AmbiguousParameter",
                matchingParameters);
        }

        [DoesNotReturn]
        internal static void ThrowRuntimeDefinedParameterNameMismatch(
            InvocationInfo invocationInfo,
            string parameterName,
            string key)
        {
            throw new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                parameterName,
                null,
                null,
                ParameterBinderStrings.RuntimeDefinedParameterNameMismatch,
                "RuntimeDefinedParameterNameMismatch",
                key);
        }

        [DoesNotReturn]
        internal static void ThrowMismatchedPSTypeName(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string psTypeName)
        {
            throw new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.MismatchedPSTypeName,
                "MismatchedPSTypeName",
                psTypeName);
        }

        internal static ParameterBindingException NewNamedParameterNotFound(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type typeSpecified)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                null,
                typeSpecified,
                ParameterBinderStrings.NamedParameterNotFound,
                "NamedParameterNotFound");
        }

        internal static ParameterBindingException NewAmbiguousParameterSet(InvocationInfo invocationInfo)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                null,
                null,
                null,
                ParameterBinderStrings.AmbiguousParameterSet,
                "AmbiguousParameterSet");
        }

        internal static ParameterBindingException NewPositionalParameterNotFound(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string argument,
            Type typeSpecified)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                argument,
                null,
                typeSpecified,
                ParameterBinderStrings.PositionalParameterNotFound,
                "PositionalParameterNotFound");
        }

        internal static ParameterBindingException NewMismatchedPSTypeName(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string psTypeName)
        {
            return new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.MismatchedPSTypeName,
                "MismatchedPSTypeName",
                psTypeName);
        }

        /// <summary>
        /// Throws a <see cref="ParameterBindingException"/> or
        /// <see cref="ParameterBindingArgumentTransformationException"/> for a mismatched PSTypeName.
        /// When <paramref name="retryOtherBindingAfterFailure"/> is <c>false</c>, a
        /// <see cref="ParameterBindingArgumentTransformationException"/> is thrown; otherwise a
        /// <see cref="ParameterBindingException"/> is thrown.
        /// </summary>
        [DoesNotReturn]
        internal static void ThrowMismatchedPSTypeName(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string commandName,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string psTypeName,
            bool retryOtherBindingAfterFailure)
        {
            PSInvalidCastException innerException = new PSInvalidCastException(
                nameof(ErrorCategory.InvalidArgument),
                null,
                ParameterBinderStrings.MismatchedPSTypeName,
                commandName,
                parameterName,
                parameterType,
                typeSpecified,
                0,
                0,
                psTypeName);

            if (!retryOtherBindingAfterFailure)
            {
                throw ParameterBindingArgumentTransformationException.NewMismatchedPSTypeName(
                    innerException,
                    invocationInfo,
                    errorPosition,
                    parameterName,
                    parameterType,
                    typeSpecified,
                    psTypeName);
            }

            throw NewMismatchedPSTypeName(
                innerException,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                psTypeName);
        }

        internal static ParameterBindingException NewParameterNotInParameterSet(
            InvocationInfo invocationInfo,
            string parameterName,
            string parameterSetName)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                parameterName,
                null,
                null,
                ParameterBinderStrings.ParameterNotInParameterSet,
                "ParameterNotInParameterSet",
                parameterSetName);
        }

        internal static ParameterBindingException NewGetDynamicParametersException(
            Exception innerException,
            InvocationInfo invocationInfo,
            string errorMessage)
        {
            return new ParameterBindingException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                null,
                null,
                null,
                ParameterBinderStrings.GetDynamicParametersException,
                "GetDynamicParametersException",
                errorMessage);
        }

        internal static ParameterBindingException NewAmbiguousPositionalParameterNoName(InvocationInfo invocationInfo)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                null,
                null,
                null,
                null,
                ParameterBinderStrings.AmbiguousPositionalParameterNoName,
                "AmbiguousPositionalParameterNoName");
        }

        internal static ParameterBindingException NewParameterAlreadyBound(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                null,
                null,
                ParameterBinderStrings.ParameterAlreadyBound,
                nameof(ParameterBinderStrings.ParameterAlreadyBound));
        }

        internal static ParameterBindingException NewMissingArgument(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType)
        {
            return new ParameterBindingException(
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                null,
                ParameterBinderStrings.MissingArgument,
                "MissingArgument");
        }

        internal static ParameterBindingException NewParameterBindingException(
            Exception innerException,
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
        {
            return new ParameterBindingException(
                innerException,
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args);
        }

        internal static ParameterBindingException NewElaboratedBindingException(
            Exception innerException,
            ParameterBindingException source,
            string resourceString,
            params object[] args)
        {
            return new ParameterBindingException(
                innerException,
                source,
                resourceString,
                args);
        }

        #endregion ThrowHelpers

        #region Properties
        /// <summary>
        /// Gets the message for the exception.
        /// </summary>
        public override string Message
        {
            get { return _message ??= BuildMessage(); }
        }

        private string _message;

        /// <summary>
        /// Gets the name of the parameter that the parameter binding
        /// error was encountered on.
        /// </summary>
        public string ParameterName
        {
            get
            {
                return _parameterName;
            }
        }

        private readonly string _parameterName = string.Empty;

        /// <summary>
        /// Gets the type the parameter is expecting.
        /// </summary>
        public Type ParameterType
        {
            get
            {
                return _parameterType;
            }
        }

        private readonly Type _parameterType;

        /// <summary>
        /// Gets the Type that was specified as the parameter value.
        /// </summary>
        public Type TypeSpecified
        {
            get
            {
                return _typeSpecified;
            }
        }

        private readonly Type _typeSpecified;

        /// <summary>
        /// Gets the errorId of this ParameterBindingException.
        /// </summary>
        public string ErrorId
        {
            get
            {
                return _errorId;
            }
        }

        private readonly string _errorId;

        /// <summary>
        /// Gets the line in the script at which the error occurred.
        /// </summary>
        public Int64 Line
        {
            get
            {
                return _line;
            }
        }

        private readonly Int64 _line = Int64.MinValue;

        /// <summary>
        /// Gets the offset on the line in the script at which the error occurred.
        /// </summary>
        public Int64 Offset
        {
            get
            {
                return _offset;
            }
        }

        private readonly Int64 _offset = Int64.MinValue;

        /// <summary>
        /// Gets the invocation information about the command.
        /// </summary>
        public InvocationInfo CommandInvocation
        {
            get
            {
                return _invocationInfo;
            }
        }

        private readonly InvocationInfo _invocationInfo;
        #endregion Properties

        #region private

        private readonly string _resourceString;
        private readonly object[] _args = Array.Empty<object>();
        private readonly string _commandName;

        private string BuildMessage()
        {
            object[] messageArgs = Array.Empty<object>();

            if (_args != null)
            {
                messageArgs = new object[_args.Length + 6];
                messageArgs[0] = _commandName;
                messageArgs[1] = _parameterName;
                messageArgs[2] = _parameterType;
                messageArgs[3] = _typeSpecified;
                messageArgs[4] = _line;
                messageArgs[5] = _offset;
                _args.CopyTo(messageArgs, 6);
            }

            string result = string.Empty;

            if (!string.IsNullOrEmpty(_resourceString))
            {
                result = StringUtil.Format(_resourceString, messageArgs);
            }

            return result;
        }

        #endregion Private
    }

    internal class ParameterBindingValidationException : ParameterBindingException
    {
        #region Preferred constructors

        /// <summary>
        /// Constructs a ParameterBindingValidationException.
        /// </summary>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingValidationException(
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
        }

        /// <summary>
        /// Constructs a ParameterBindingValidationException.
        /// </summary>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="invocationInfo"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceBaseName"/> or <paramref name="errorIdAndResourceId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingValidationException(
            Exception innerException,
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                innerException,
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
            if (innerException is ValidationMetadataException validationException && validationException.SwallowException)
            {
                _swallowException = true;
            }
        }

        [DoesNotReturn]
        internal static void ThrowParameterArgumentValidationError(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingValidationException(
                innerException,
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterArgumentValidationError,
                "ParameterArgumentValidationError",
                errorMessage);
        }

        [DoesNotReturn]
        internal static void ThrowParameterArgumentValidationErrorNullNotAllowed(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified)
        {
            throw new ParameterBindingValidationException(
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterArgumentValidationErrorNullNotAllowed,
                "ParameterArgumentValidationErrorNullNotAllowed");
        }

        [DoesNotReturn]
        internal static void ThrowParameterArgumentValidationErrorEmptyStringNotAllowed(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified)
        {
            throw new ParameterBindingValidationException(
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterArgumentValidationErrorEmptyStringNotAllowed,
                "ParameterArgumentValidationErrorEmptyStringNotAllowed");
        }

        [DoesNotReturn]
        internal static void ThrowValidateNullOrEmpty(
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId)
        {
            throw new ParameterBindingValidationException(
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId);
        }
        #endregion Preferred constructors

        #region serialization
        /// <summary>
        /// Constructs a ParameterBindingValidationException from serialized data.
        /// </summary>
        /// <param name="info">
        /// serialization information
        /// </param>
        /// <param name="context">
        /// streaming context
        /// </param>
        [Obsolete("Legacy serialization support is deprecated since .NET 8", DiagnosticId = "SYSLIB0051")]
        protected ParameterBindingValidationException(
            SerializationInfo info,
            StreamingContext context)
        {
            throw new NotSupportedException();
        }

        #endregion serialization

        #region Property

        /// <summary>
        /// Make the positional binding ignore this validation exception when it's set to true.
        /// </summary>
        /// <remarks>
        /// This property is only used internally in the positional binding phase
        /// </remarks>
        internal bool SwallowException
        {
            get { return _swallowException; }
        }

        private readonly bool _swallowException = false;

        #endregion Property
    }

    internal class ParameterBindingArgumentTransformationException : ParameterBindingException
    {
        #region Preferred constructors

        /// <summary>
        /// Constructs a ParameterBindingArgumentTransformationException.
        /// </summary>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingArgumentTransformationException(
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
        }

        /// <summary>
        /// Constructs a ParameterBindingArgumentTransformationException.
        /// </summary>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="invocationInfo"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingArgumentTransformationException(
            Exception innerException,
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                innerException,
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
        }

        [DoesNotReturn]
        internal static void ThrowParameterArgumentTransformationError(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            throw new ParameterBindingArgumentTransformationException(
                innerException,
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterArgumentTransformationError,
                "ParameterArgumentTransformationError",
                errorMessage);
        }

        internal static new ParameterBindingArgumentTransformationException NewMismatchedPSTypeName(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string psTypeName)
        {
            return new ParameterBindingArgumentTransformationException(
                innerException,
                ErrorCategory.InvalidArgument,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.MismatchedPSTypeName,
                "MismatchedPSTypeName",
                psTypeName);
        }

        internal static ParameterBindingArgumentTransformationException NewParameterArgumentTransformationErrorMessageOnly(
            Exception innerException,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string errorMessage)
        {
            return new ParameterBindingArgumentTransformationException(
                innerException,
                ErrorCategory.InvalidData,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                ParameterBinderStrings.ParameterArgumentTransformationErrorMessageOnly,
                "ParameterArgumentTransformationErrorMessageOnly",
                errorMessage);
        }
        #endregion Preferred constructors
        #region serialization
        /// <summary>
        /// Constructs a ParameterBindingArgumentTransformationException using serialized data.
        /// </summary>
        /// <param name="info">
        /// serialization information
        /// </param>
        /// <param name="context">
        /// streaming context
        /// </param>
        [Obsolete("Legacy serialization support is deprecated since .NET 8", DiagnosticId = "SYSLIB0051")]
        protected ParameterBindingArgumentTransformationException(
            SerializationInfo info,
            StreamingContext context)
        {
            throw new NotSupportedException();
        }

        #endregion serialization
    }

    internal class ParameterBindingParameterDefaultValueException : ParameterBindingException
    {
        #region Preferred constructors

        /// <summary>
        /// Constructs a ParameterBindingParameterDefaultValueException.
        /// </summary>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingParameterDefaultValueException(
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
        }

        /// <summary>
        /// Constructs a ParameterBindingParameterDefaultValueException.
        /// </summary>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        /// <param name="errorCategory">
        /// The category for the error.
        /// </param>
        /// <param name="invocationInfo">
        /// The information about the command that encountered the error.
        ///
        /// InvocationInfo.MyCommand.Name == {0}
        /// </param>
        /// <param name="errorPosition">
        /// The position for the command or parameter that caused the error.
        ///
        /// token.LineNumber == {4}
        /// token.OffsetInLine == {5}
        /// </param>
        /// <param name="parameterName">
        /// The parameter on which binding caused the error.
        ///
        /// parameterName == {1}
        /// </param>
        /// <param name="parameterType">
        /// The Type the parameter was expecting.
        ///
        /// parameterType == {2}
        /// </param>
        /// <param name="typeSpecified">
        /// The Type that was attempted to be bound to the parameter.
        ///
        /// typeSpecified == {3}
        /// </param>
        /// <param name="resourceString">
        /// The format string for the exception message.
        /// </param>
        /// <param name="errorId">
        /// The error ID.
        /// </param>
        /// <param name="args">
        /// Additional arguments to pass to the format string.
        ///
        /// starts at {6}
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// If <paramref name="invocationInfo"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// If <paramref name="resourceString"/> or <paramref name="errorId"/>
        /// is null or empty.
        /// </exception>
        internal ParameterBindingParameterDefaultValueException(
            Exception innerException,
            ErrorCategory errorCategory,
            InvocationInfo invocationInfo,
            IScriptExtent errorPosition,
            string parameterName,
            Type parameterType,
            Type typeSpecified,
            string resourceString,
            string errorId,
            params object[] args)
            : base(
                innerException,
                errorCategory,
                invocationInfo,
                errorPosition,
                parameterName,
                parameterType,
                typeSpecified,
                resourceString,
                errorId,
                args)
        {
        }

        [DoesNotReturn]
        internal static void ThrowGetDefaultValueFailed(
            Exception innerException,
            InvocationInfo invocationInfo,
            string parameterName,
            string errorMessage)
        {
            throw new ParameterBindingParameterDefaultValueException(
                innerException,
                ErrorCategory.ReadError,
                invocationInfo,
                null,
                parameterName,
                null,
                null,
                "ParameterBinderStrings",
                "GetDefaultValueFailed",
                errorMessage);
        }
        #endregion Preferred constructors

        #region serialization
        /// <summary>
        /// Constructs a ParameterBindingParameterDefaultValueException using serialized data.
        /// </summary>
        /// <param name="info">
        /// serialization information
        /// </param>
        /// <param name="context">
        /// streaming context
        /// </param>
        [Obsolete("Legacy serialization support is deprecated since .NET 8", DiagnosticId = "SYSLIB0051")]
        protected ParameterBindingParameterDefaultValueException(
            SerializationInfo info,
            StreamingContext context)
        {
            throw new NotSupportedException();
        }

        #endregion serialization
    }
}
