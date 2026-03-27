// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Reflection;

namespace System.Management.Automation;

/// <summary>
/// Encapsulates parameter type coercion and collection encoding logic.
/// </summary>
internal sealed class ParameterTypeCoercer
{
    [TraceSource("ParameterBinderBase", "A abstract helper class for the CommandProcessor that binds parameters to the specified object.")]
    private static readonly PSTraceSource s_tracer = PSTraceSource.GetTracer("ParameterBinderBase", "A abstract helper class for the CommandProcessor that binds parameters to the specified object.");

    [TraceSource("ParameterBinding", "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.")]
    internal static readonly PSTraceSource bindingTracer =
        PSTraceSource.GetTracer(
            "ParameterBinding",
            "Traces the process of binding the arguments to the parameters of cmdlets, scripts, and applications.",
            false);

    private readonly InvocationInfo _invocationInfo;
    private readonly ExecutionContext _context;
    private readonly InternalCommand _command;

    internal ParameterTypeCoercer(
        InvocationInfo invocationInfo,
        ExecutionContext context,
        InternalCommand command)
    {
        _invocationInfo = invocationInfo;
        _context = context;
        _command = command;
    }

    /// <summary>
    /// Coerces the argument type to the parameter value type as needed.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="argument"/> or <paramref name="toType"/> is null.
    /// </exception>
    /// <exception cref="ParameterBindingException">
    /// If the argument value is missing and the parameter is not a bool or SwitchParameter.
    /// or
    /// If the argument value could not be converted to the parameter type.
    /// </exception>
    internal object CoerceTypeAsNeeded(
        CommandParameterInternal argument,
        string parameterName,
        Type toType,
        ParameterCollectionTypeInformation collectionTypeInfo,
        object currentValue)
    {
        if (argument == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(argument));
        }

        if (toType == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(toType));
        }

        collectionTypeInfo ??= new ParameterCollectionTypeInformation(toType);

        object originalValue = currentValue;
        object result = currentValue;

        using (bindingTracer.TraceScope(
            "COERCE arg to [{0}]", toType))
        {
            object FinalizeResult(object value)
            {
                if (value != null)
                {
                    ExecutionContext.PropagateInputSource(originalValue, value, _context.LanguageMode);
                }

                return value;
            }

            Type argumentType = null;
            try
            {
                if (IsNullParameterValue(currentValue))
                {
                    result = HandleNullParameterForSpecialTypes(argument, parameterName, toType, currentValue);
                    return FinalizeResult(result);
                }

                argumentType = currentValue.GetType();

                if (toType.IsAssignableFrom(argumentType))
                {
                    bindingTracer.WriteLine(
                        "Parameter and arg types the same, no coercion is needed.");

                    result = currentValue;
                    return FinalizeResult(result);
                }

                bindingTracer.WriteLine("Trying to convert argument value from {0} to {1}", argumentType, toType);

                if (toType == typeof(PSObject))
                {
                    if (_command != null &&
                        currentValue == _command.CurrentPipelineObject.BaseObject)
                    {
                        currentValue = _command.CurrentPipelineObject;
                    }

                    bindingTracer.WriteLine(
                        "The parameter is of type [{0}] and the argument is an PSObject, so the parameter value is the argument value wrapped into an PSObject.",
                        toType);
                    result = LanguagePrimitives.AsPSObjectOrNull(currentValue);
                    return FinalizeResult(result);
                }

                if (toType == typeof(string) &&
                    argumentType == typeof(PSObject))
                {
                    PSObject currentValueAsPSObject = (PSObject)currentValue;

                    if (currentValueAsPSObject == AutomationNull.Value)
                    {
                        bindingTracer.WriteLine(
                            "CONVERT a null PSObject to a null string.");
                        result = null;
                        return FinalizeResult(result);
                    }
                }

                if (toType == typeof(bool) || toType == typeof(SwitchParameter) ||
                    toType == typeof(bool?))
                {
                    result = CoerceToBooleanOrSwitch(currentValue, argumentType, toType, argument, parameterName);
                    return FinalizeResult(result);
                }

                if (collectionTypeInfo.ParameterCollectionType == ParameterCollectionType.ICollectionGeneric
                    || collectionTypeInfo.ParameterCollectionType == ParameterCollectionType.IList)
                {
                    object currentValueToConvert = PSObject.Base(currentValue);
                    if (currentValueToConvert != null)
                    {
                        ConversionRank rank = LanguagePrimitives.GetConversionRank(currentValueToConvert.GetType(), toType);
                        if (rank == ConversionRank.Constructor || rank == ConversionRank.ImplicitCast || rank == ConversionRank.ExplicitCast)
                        {
                            if (LanguagePrimitives.TryConvertTo(currentValue, toType, CultureInfo.CurrentCulture, out result))
                            {
                                return FinalizeResult(result);
                            }
                        }
                    }
                }

                if (collectionTypeInfo.ParameterCollectionType != ParameterCollectionType.NotCollection)
                {
                    bindingTracer.WriteLine(
                        "ENCODING arg into collection");

                    bool ignored = false;
                    result =
                        EncodeCollection(
                            argument,
                            parameterName,
                            collectionTypeInfo,
                            toType,
                            currentValue,
                            (collectionTypeInfo.ElementType != null),
                            out ignored);

                    return FinalizeResult(result);
                }

                if (ParameterBinderBase.GetIList(currentValue) != null &&
                    toType != typeof(object) &&
                    toType != typeof(PSObject) &&
                    toType != typeof(PSListModifier) &&
                    (!toType.IsGenericType || toType.GetGenericTypeDefinition() != typeof(PSListModifier<>)) &&
                    (!toType.IsGenericType || toType.GetGenericTypeDefinition() != typeof(FlagsExpression<>)) &&
                    !toType.IsEnum)
                {
                    throw new NotSupportedException();
                }

                result = CoerceWithLanguagePrimitives(currentValue, toType, argument, parameterName, argumentType);

                return FinalizeResult(result);
            }
            catch (NotSupportedException notSupported)
            {
                bindingTracer.TraceError(
                    "ERROR: COERCE FAILED: arg [{0}] could not be converted to the parameter type [{1}]",
                    result ?? "null",
                    toType);

                ParameterBindingException.ThrowCannotConvertArgument(
                    notSupported,
                    _invocationInfo,
                    GetErrorExtent(argument),
                    parameterName,
                    toType,
                    argumentType,
                    result ?? "null",
                    notSupported.Message);

                throw new InvalidOperationException();
            }
            catch (PSInvalidCastException invalidCast)
            {
                bindingTracer.TraceError(
                  "ERROR: COERCE FAILED: arg [{0}] could not be converted to the parameter type [{1}]",
                  result ?? "null",
                  toType);

                ParameterBindingException.ThrowCannotConvertArgumentNoMessage(
                    invalidCast,
                    _invocationInfo,
                    GetErrorExtent(argument),
                    parameterName,
                    toType,
                    argumentType,
                    invalidCast.Message);

                throw new InvalidOperationException();
            }
        }
    }

    internal static bool IsNullParameterValue(object currentValue)
    {
        return currentValue == null ||
               currentValue == AutomationNull.Value ||
               currentValue == UnboundParameter.Value;
    }

    private object HandleNullParameterForSpecialTypes(
        CommandParameterInternal argument,
        string parameterName,
        Type toType,
        object currentValue)
    {
        object result = null;

        if (toType == typeof(bool))
        {
            bindingTracer.WriteLine(
                    "ERROR: No argument is specified for parameter and parameter type is BOOL");

            ParameterBindingValidationException.ThrowParameterArgumentValidationErrorNullNotAllowed(
                _invocationInfo,
                GetErrorExtent(argument),
                parameterName,
                toType,
                null);
        }
        else
            if (toType == typeof(SwitchParameter))
        {
            bindingTracer.WriteLine(
                "Arg is null or not present, parameter type is SWITCHPARAMTER, value is true.");
            result = SwitchParameter.Present;
        }
        else if (currentValue == UnboundParameter.Value)
        {
            bindingTracer.TraceError(
                "ERROR: No argument was specified for the parameter and the parameter is not of type bool");

            ParameterBindingException.ThrowMissingArgument(
                _invocationInfo,
                GetParameterErrorExtent(argument),
                parameterName,
                toType);
        }
        else
        {
            bindingTracer.WriteLine(
                "Arg is null, parameter type not bool or SwitchParameter, value is null.");
            result = null;
        }

        return result;
    }

    /// <summary>
    /// Coerces a value to bool or SwitchParameter when the target type requires it.
    /// </summary>
    private object CoerceToBooleanOrSwitch(
        object currentValue,
        Type argumentType,
        Type toType,
        CommandParameterInternal argument,
        string parameterName)
    {
        Type boType;
        if (argumentType == typeof(PSObject))
        {
            PSObject currentValueAsPSObject = (PSObject)currentValue;
            currentValue = currentValueAsPSObject.BaseObject;

            if (currentValue is SwitchParameter)
            {
                currentValue = ((SwitchParameter)currentValue).IsPresent;
            }

            boType = currentValue.GetType();
        }
        else
        {
            boType = argumentType;
        }

        if (boType == typeof(bool))
        {
            return LanguagePrimitives.IsBooleanType(toType)
                ? ParserOps.BoolToObject((bool)currentValue)
                : new SwitchParameter((bool)currentValue);
        }

        if (boType == typeof(int))
        {
            bool isTrue = (int)LanguagePrimitives.ConvertTo(currentValue, typeof(int), CultureInfo.InvariantCulture) != 0;
            return LanguagePrimitives.IsBooleanType(toType)
                ? ParserOps.BoolToObject(isTrue)
                : new SwitchParameter(isTrue);
        }

        if (LanguagePrimitives.IsNumeric(boType.GetTypeCode()))
        {
            double currentValueAsDouble = (double)LanguagePrimitives.ConvertTo(
                currentValue, typeof(double), CultureInfo.InvariantCulture);
            bool isTrue = currentValueAsDouble != 0;
            return LanguagePrimitives.IsBooleanType(toType)
                ? ParserOps.BoolToObject(isTrue)
                : new SwitchParameter(isTrue);
        }

        ParameterBindingException.ThrowCannotConvertArgument(
            null,
            _invocationInfo,
            GetErrorExtent(argument),
            parameterName,
            toType,
            argumentType,
            boType,
            string.Empty);

        throw new InvalidOperationException();
    }

    /// <summary>
    /// Converts a value using LanguagePrimitives.ConvertTo, handling language mode
    /// switching for parameters that allow it.
    /// </summary>
    private object CoerceWithLanguagePrimitives(
        object currentValue,
        Type toType,
        CommandParameterInternal argument,
        string parameterName,
        Type argumentType)
    {
        _ = argument;
        _ = parameterName;
        _ = argumentType;

        bindingTracer.WriteLine(
            "CONVERT arg type to param type using LanguagePrimitives.ConvertTo");

        var currentLanguageMode = _context.LanguageMode;
        bool changeLanguageModeForTrustedCommand =
            currentLanguageMode == PSLanguageMode.ConstrainedLanguage &&
            _command.CommandInfo.DefiningLanguageMode == PSLanguageMode.FullLanguage;
        bool oldLangModeTransitionStatus = _context.LanguageModeTransitionInParameterBinding;

        object result;
        try
        {
            if (changeLanguageModeForTrustedCommand)
            {
                _context.LanguageMode = PSLanguageMode.FullLanguage;
                _context.LanguageModeTransitionInParameterBinding = true;
            }

            result = LanguagePrimitives.ConvertTo(currentValue, toType, CultureInfo.CurrentCulture);
        }
        finally
        {
            if (changeLanguageModeForTrustedCommand)
            {
                _context.LanguageMode = currentLanguageMode;
                _context.LanguageModeTransitionInParameterBinding = oldLangModeTransitionStatus;
            }
        }

        bindingTracer.WriteLine(
            "CONVERT SUCCESSFUL using LanguagePrimitives.ConvertTo: [{0}]",
            result is null ? "null" : result.ToString());

        return result;
    }

    /// <summary>
    /// Takes the current value specified and converts or adds it to
    /// a collection of the appropriate type.
    /// </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1505:AvoidUnmaintainableCode")]
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Consider Simplifying it")]
    internal object EncodeCollection(
        CommandParameterInternal argument,
        string parameterName,
        ParameterCollectionTypeInformation collectionTypeInformation,
        Type toType,
        object currentValue,
        bool coerceElementTypeIfNeeded,
        out bool coercionRequired)
    {
        object originalValue = currentValue;
        object result = null;
        coercionRequired = false;

        bindingTracer.WriteLine(
            "Binding collection parameter {0}: argument type [{1}], parameter type [{2}], collection type {3}, element type [{4}], {5}",
            parameterName,
            (currentValue == null) ? "null" : currentValue.GetType().Name,
            toType,
            collectionTypeInformation.ParameterCollectionType,
            collectionTypeInformation.ElementType,
            coerceElementTypeIfNeeded ? "coerceElementType" : "no coerceElementType");

        if (currentValue == null)
        {
            return result;
        }

        int numberOfElements = 1;
        Type collectionElementType = collectionTypeInformation.ElementType;

        IList currentValueAsIList = ParameterBinderBase.GetIList(currentValue);

        if (currentValueAsIList != null)
        {
            numberOfElements = currentValueAsIList.Count;

            s_tracer.WriteLine("current value is an IList with {0} elements", numberOfElements);
            bindingTracer.WriteLine(
                "Arg is IList with {0} elements",
                numberOfElements);
        }

        var createdCollection = CreateTargetCollection(
            argument,
            parameterName,
            collectionTypeInformation,
            toType,
            currentValue,
            numberOfElements,
            collectionElementType);

        if (createdCollection.collection is null)
        {
            return result;
        }

        collectionElementType = createdCollection.elementType;

        PopulateCollection(
            argument,
            parameterName,
            collectionTypeInformation,
            toType,
            currentValue,
            currentValueAsIList,
            createdCollection.collection,
            createdCollection.asIList,
            createdCollection.addMethod,
            collectionElementType,
            coerceElementTypeIfNeeded,
            createdCollection.isSystemDotArray,
            ref coercionRequired);

        if (coercionRequired)
        {
            return result;
        }

        result = createdCollection.collection;

        ExecutionContext.PropagateInputSource(originalValue, result, _context.LanguageMode);

        return result;
    }

    /// <summary>
    /// Creates a collection instance for the destination parameter type.
    /// </summary>
    private (object collection, IList asIList, MethodInfo addMethod, bool isSystemDotArray, Type elementType) CreateTargetCollection(
        CommandParameterInternal argument,
        string parameterName,
        ParameterCollectionTypeInformation collectionTypeInformation,
        Type toType,
        object currentValue,
        int numberOfElements,
        Type collectionElementType)
    {
        object resultCollection = null;
        IList resultAsIList = null;
        MethodInfo addMethod = null;

        bool isSystemDotArray = (toType == typeof(System.Array));

        if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.Array ||
            isSystemDotArray)
        {
            if (isSystemDotArray)
            {
                collectionElementType = typeof(object);
            }

            bindingTracer.WriteLine(
                "Creating array with element type [{0}] and {1} elements",
                collectionElementType,
                numberOfElements);

            resultCollection = resultAsIList =
                (IList)Array.CreateInstance(
                    collectionElementType,
                    numberOfElements);
        }
        else if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.IList ||
                 collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.ICollectionGeneric)
        {
            bindingTracer.WriteLine(
                "Creating collection [{0}]",
                toType);

            bool errorOccurred = false;
            Exception error = null;
            try
            {
                resultCollection =
                    Activator.CreateInstance(
                        toType,
                        0,
                        null,
                        Array.Empty<object>(),
                        CultureInfo.InvariantCulture);
                if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.IList)
                {
                    resultAsIList = (IList)resultCollection;
                }
                else
                {
                    Diagnostics.Assert(
                        collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.ICollectionGeneric,
                        "invalid collection type"
                        );
                    const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance;
                    Type elementType = collectionTypeInformation.ElementType;
                    Diagnostics.Assert(elementType != null, "null ElementType");
                    Exception getMethodError = null;
                    try
                    {
                        addMethod = toType.GetMethod("Add", bindingFlags, null, new Type[1] { elementType }, null);
                    }
                    catch (AmbiguousMatchException e)
                    {
                        bindingTracer.WriteLine("Ambiguous match to Add(T) for type {0}: {1}", toType.FullName, e.Message);
                        getMethodError = e;
                    }
                    catch (ArgumentException e)
                    {
                        bindingTracer.WriteLine(
                            "ArgumentException matching Add(T) for type {0}: {1}", toType.FullName, e.Message);
                        getMethodError = e;
                    }

                    if (addMethod == null)
                    {
                        ParameterBindingException.ThrowCannotExtractAddMethod(
                            getMethodError,
                            _invocationInfo,
                            GetErrorExtent(argument),
                            parameterName,
                            toType,
                            currentValue.GetType(),
                            (getMethodError == null) ? string.Empty : getMethodError.Message);
                    }
                }
            }
            catch (Exception e) when (
                e is ArgumentException
                or NotSupportedException
                or TargetInvocationException
                or MethodAccessException
                or MemberAccessException
                or System.Runtime.InteropServices.InvalidComObjectException
                or System.Runtime.InteropServices.COMException
                or TypeLoadException)
            {
                errorOccurred = true;
                error = e;
            }

            if (errorOccurred)
            {
                ParameterBindingException.ThrowCannotConvertArgument(
                    error,
                    _invocationInfo,
                    GetErrorExtent(argument),
                    parameterName,
                    toType,
                    currentValue.GetType(),
                    "null",
                    error.Message);
            }
        }
        else
        {
            Diagnostics.Assert(
                false,
                "This method should not be called for a parameter that is not a collection");
        }

        return (resultCollection, resultAsIList, addMethod, isSystemDotArray, collectionElementType);
    }

    /// <summary>
    /// Populates the target collection with source values, coercing elements when needed.
    /// </summary>
    private void PopulateCollection(
        CommandParameterInternal argument,
        string parameterName,
        ParameterCollectionTypeInformation collectionTypeInformation,
        Type toType,
        object currentValue,
        IList currentValueAsIList,
        object resultCollection,
        IList resultAsIList,
        MethodInfo addMethod,
        Type collectionElementType,
        bool coerceElementTypeIfNeeded,
        bool isSystemDotArray,
        ref bool coercionRequired)
    {
        if (currentValueAsIList != null)
        {
            int arrayIndex = 0;

            bindingTracer.WriteLine(
                "Argument type {0} is IList",
                currentValue.GetType());

            foreach (object valueElement in currentValueAsIList)
            {
                object currentValueElement = PSObject.Base(valueElement);

                if (coerceElementTypeIfNeeded)
                {
                    bindingTracer.WriteLine(
                        "COERCE collection element from type {0} to type {1}",
                        (valueElement == null) ? "null" : valueElement.GetType().Name,
                        collectionElementType);

                    currentValueElement =
                        CoerceTypeAsNeeded(
                            argument,
                            parameterName,
                            collectionElementType,
                            null,
                            valueElement);
                }
                else if (collectionElementType != null && currentValueElement != null)
                {
                    Type currentValueElementType = currentValueElement.GetType();
                    Type desiredElementType = collectionElementType;

                    if (currentValueElementType != desiredElementType &&
                        !currentValueElementType.IsSubclassOf(desiredElementType))
                    {
                        bindingTracer.WriteLine(
                            "COERCION REQUIRED: Did not attempt to coerce collection element from type {0} to type {1}",
                            (valueElement == null) ? "null" : valueElement.GetType().Name,
                            collectionElementType);

                        coercionRequired = true;
                        return;
                    }
                }

                AddElementToCollection(
                    argument,
                    parameterName,
                    collectionTypeInformation,
                    toType,
                    resultCollection,
                    resultAsIList,
                    addMethod,
                    isSystemDotArray,
                    currentValueElement,
                    arrayIndex);

                if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.Array || isSystemDotArray)
                {
                    arrayIndex++;
                }
            }

            return;
        }

        bindingTracer.WriteLine(
            "Argument type {0} is not IList, treating this as scalar",
            currentValue.GetType().Name);

        if (collectionElementType != null)
        {
            if (coerceElementTypeIfNeeded)
            {
                bindingTracer.WriteLine(
                    "Coercing scalar arg value to type {0}",
                    collectionElementType);

                currentValue =
                    CoerceTypeAsNeeded(
                        argument,
                        parameterName,
                        collectionElementType,
                        null,
                        currentValue);
            }
            else
            {
                Type currentValueElementType = currentValue.GetType();
                Type desiredElementType = collectionElementType;

                if (currentValueElementType != desiredElementType &&
                    !currentValueElementType.IsSubclassOf(desiredElementType))
                {
                    bindingTracer.WriteLine(
                        "COERCION REQUIRED: Did not coerce scalar arg value to type {1}",
                        collectionElementType);

                    coercionRequired = true;
                    return;
                }
            }
        }

        AddElementToCollection(
            argument,
            parameterName,
            collectionTypeInformation,
            toType,
            resultCollection,
            resultAsIList,
            addMethod,
            isSystemDotArray,
            currentValue,
            0);
    }

    private void AddElementToCollection(
        CommandParameterInternal argument,
        string parameterName,
        ParameterCollectionTypeInformation collectionTypeInformation,
        Type toType,
        object resultCollection,
        IList resultAsIList,
        MethodInfo addMethod,
        bool isSystemDotArray,
        object value,
        int arrayIndex)
    {
        try
        {
            if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.Array ||
                isSystemDotArray)
            {
                bindingTracer.WriteLine(
                    "Adding element of type {0} to array position {1}",
                    (value == null) ? "null" : value.GetType().Name,
                    arrayIndex);
                resultAsIList[arrayIndex] = value;
            }
            else if (collectionTypeInformation.ParameterCollectionType == ParameterCollectionType.IList)
            {
                bindingTracer.WriteLine(
                    "Adding element of type {0} via IList.Add",
                    (value == null) ? "null" : value.GetType().Name);
                resultAsIList.Add(value);
            }
            else
            {
                bindingTracer.WriteLine(
                    "Adding element of type {0} via ICollection<T>::Add()",
                    (value == null) ? "null" : value.GetType().Name);
                addMethod.Invoke(resultCollection, new object[1] { value });
            }
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException &&
                error.InnerException != null)
            {
                error = error.InnerException;
            }

            ParameterBindingException.ThrowCannotConvertArgument(
                error,
                _invocationInfo,
                GetErrorExtent(argument),
                parameterName,
                toType,
                value?.GetType(),
                value ?? "null",
                error.Message);
        }
    }

    private IScriptExtent GetErrorExtent(CommandParameterInternal cpi)
    {
        var result = cpi.ErrorExtent;
        if (result == PositionUtilities.EmptyExtent)
        {
            result = _invocationInfo.ScriptPosition;
        }

        return result;
    }

    private IScriptExtent GetParameterErrorExtent(CommandParameterInternal cpi)
    {
        var result = cpi.ParameterExtent;
        if (result == PositionUtilities.EmptyExtent)
        {
            result = _invocationInfo.ScriptPosition;
        }

        return result;
    }
}
