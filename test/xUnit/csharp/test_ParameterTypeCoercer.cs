// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Direct unit tests for <see cref="ParameterTypeCoercer"/> that instantiate the class
    /// and call its methods without going through <c>PowerShell.Create()</c>.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class ParameterTypeCoercerTests
    {
        private readonly ExecutionContext _executionContext;
        private readonly InvocationInfo _invocationInfo;
        private readonly ParameterTypeCoercer _coercer;

        public ParameterTypeCoercerTests()
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            PSHost host = new DefaultHost(culture, culture);
            InitialSessionState iss = InitialSessionState.CreateDefault2();
            var engine = new AutomationEngine(host, iss);
            _executionContext = new ExecutionContext(engine, host, iss);
            _invocationInfo = new InvocationInfo(new CmdletInfo("Get-Variable", typeof(GetVariableCommand)), null);
            _coercer = new ParameterTypeCoercer(_invocationInfo, _executionContext, null);
        }

        private static CommandParameterInternal MakeArg(string name, object value)
        {
            return CommandParameterInternal.CreateParameterWithArgument(
                null, name, "-" + name + ":", null, value, false);
        }

        // ── IsNullParameterValue (static) ────────────────────────────────

        [Fact]
        public void IsNullParameterValue_Null_ReturnsTrue()
        {
            Assert.True(ParameterTypeCoercer.IsNullParameterValue(null));
        }

        [Fact]
        public void IsNullParameterValue_AutomationNull_ReturnsTrue()
        {
            Assert.True(ParameterTypeCoercer.IsNullParameterValue(AutomationNull.Value));
        }

        [Fact]
        public void IsNullParameterValue_UnboundParameter_ReturnsTrue()
        {
            Assert.True(ParameterTypeCoercer.IsNullParameterValue(UnboundParameter.Value));
        }

        [Fact]
        public void IsNullParameterValue_ValidObject_ReturnsFalse()
        {
            Assert.False(ParameterTypeCoercer.IsNullParameterValue(42));
            Assert.False(ParameterTypeCoercer.IsNullParameterValue("hello"));
        }

        // ── CoerceTypeAsNeeded ───────────────────────────────────────────

        [Fact]
        public void CoerceTypeAsNeeded_AssignableType_ReturnsSameValue()
        {
            var arg = MakeArg("Value", "hello");
            var result = _coercer.CoerceTypeAsNeeded(arg, "Value", typeof(object), null, "hello");
            Assert.Equal("hello", result);
        }

        [Fact]
        public void CoerceTypeAsNeeded_IntToString_Converts()
        {
            var arg = MakeArg("Text", 42);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Text", typeof(string), null, 42);
            Assert.Equal("42", result);
        }

        [Fact]
        public void CoerceTypeAsNeeded_NullToNullableInt_ReturnsNull()
        {
            var arg = MakeArg("Value", null);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Value", typeof(int?), null, null);
            Assert.Null(result);
        }

        [Fact]
        public void CoerceTypeAsNeeded_NullToBool_Throws()
        {
            var arg = MakeArg("Flag", null);
            Assert.Throws<ParameterBindingValidationException>(() =>
                _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, null));
        }

        [Fact]
        public void CoerceTypeAsNeeded_NullToSwitchParameter_ReturnsPresent()
        {
            var arg = MakeArg("Enable", null);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Enable", typeof(SwitchParameter), null, null);
            Assert.IsType<SwitchParameter>(result);
            Assert.True(((SwitchParameter)result).IsPresent);
        }

        [Fact]
        public void CoerceTypeAsNeeded_ZeroToBool_ReturnsFalse()
        {
            var arg = MakeArg("Flag", 0);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, 0);
            Assert.Equal(false, result);
        }

        [Fact]
        public void CoerceTypeAsNeeded_NonZeroIntToBool_ReturnsTrue()
        {
            var arg = MakeArg("Flag", 5);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, 5);
            Assert.Equal(true, result);
        }

        [Fact]
        public void CoerceTypeAsNeeded_StringToBool_Throws()
        {
            var arg = MakeArg("Flag", "yes");
            Assert.Throws<ParameterBindingException>(() =>
                _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, "yes"));
        }

        [Fact]
        public void CoerceTypeAsNeeded_EmptyStringToBool_Throws()
        {
            var arg = MakeArg("Flag", string.Empty);
            Assert.Throws<ParameterBindingException>(() =>
                _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, string.Empty));
        }

        [Fact]
        public void CoerceTypeAsNeeded_ToPSObject_WrapsValue()
        {
            var arg = MakeArg("Value", 42);
            var result = _coercer.CoerceTypeAsNeeded(arg, "Value", typeof(PSObject), null, 42);
            Assert.IsType<PSObject>(result);
            Assert.Equal(42, ((PSObject)result).BaseObject);
        }

        [Fact]
        public void CoerceTypeAsNeeded_UnboundParameterToBool_Throws()
        {
            // UnboundParameter.Value signals "no argument was provided" — should throw for
            // non-switch bool.
            var arg = MakeArg("Flag", UnboundParameter.Value);
            Assert.Throws<ParameterBindingValidationException>(() =>
                _coercer.CoerceTypeAsNeeded(arg, "Flag", typeof(bool), null, UnboundParameter.Value));
        }

        // ── EncodeCollection ─────────────────────────────────────────────

        [Fact]
        public void EncodeCollection_ScalarToArray_WrapsInSingleElementArray()
        {
            var arg = MakeArg("Items", "single");
            var collectionInfo = new ParameterCollectionTypeInformation(typeof(string[]));
            var result = _coercer.EncodeCollection(
                arg, "Items", collectionInfo, typeof(string[]), "single", true, out _);
            var arr = Assert.IsType<string[]>(result);
            Assert.Single(arr);
            Assert.Equal("single", arr[0]);
        }

        [Fact]
        public void EncodeCollection_ArrayToArray_PreservesAllElements()
        {
            var source = new object[] { "a", "b", "c" };
            var arg = MakeArg("Items", source);
            var collectionInfo = new ParameterCollectionTypeInformation(typeof(string[]));
            var result = _coercer.EncodeCollection(
                arg, "Items", collectionInfo, typeof(string[]), source, true, out _);
            var arr = Assert.IsType<string[]>(result);
            Assert.Equal(3, arr.Length);
            Assert.Equal("a", arr[0]);
            Assert.Equal("b", arr[1]);
            Assert.Equal("c", arr[2]);
        }

        [Fact]
        public void EncodeCollection_IntArrayToStringArray_CoercesElements()
        {
            var source = new object[] { 1, 2, 3 };
            var arg = MakeArg("Items", source);
            var collectionInfo = new ParameterCollectionTypeInformation(typeof(string[]));
            var result = _coercer.EncodeCollection(
                arg, "Items", collectionInfo, typeof(string[]), source, true, out _);
            var arr = Assert.IsType<string[]>(result);
            Assert.Equal(new[] { "1", "2", "3" }, arr);
        }

        [Fact]
        public void EncodeCollection_NullValue_ReturnsNull()
        {
            var arg = MakeArg("Items", null);
            var collectionInfo = new ParameterCollectionTypeInformation(typeof(string[]));
            var result = _coercer.EncodeCollection(
                arg, "Items", collectionInfo, typeof(string[]), null, true, out _);
            Assert.Null(result);
        }
    }
}
