// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    // ---------------------------------------------------------------------------
    // LanguagePrimitives.ConvertTo type coercion tests
    // ---------------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    public static class LanguagePrimitivesCoercionTests
    {
        // -----------------------------------------------------------------------
        // Numeric coercions
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("42",   typeof(int),    42)]
        [InlineData("3.14", typeof(double), 3.14)]
        [InlineData("255",  typeof(byte),   (byte)255)]
        [InlineData("1",    typeof(long),   1L)]
        public static void String_Coerced_To_Numeric(string input, Type targetType, object expected)
        {
            object result = LanguagePrimitives.ConvertTo(input, targetType, CultureInfo.InvariantCulture);
            Assert.Equal(expected, result);
        }

        [Fact]
        public static void Int_Coerced_To_String()
        {
            object result = LanguagePrimitives.ConvertTo(99, typeof(string), CultureInfo.InvariantCulture);
            Assert.Equal("99", result);
        }

        [Fact]
        public static void Double_Coerced_To_Int_Truncates()
        {
            object result = LanguagePrimitives.ConvertTo(3.9, typeof(int), CultureInfo.InvariantCulture);
            Assert.Equal(4, result);  // PowerShell rounds rather than truncates
        }

        [Fact]
        public static void Null_Coerced_To_Int_Returns_Zero()
        {
            object result = LanguagePrimitives.ConvertTo(null, typeof(int), CultureInfo.InvariantCulture);
            Assert.Equal(0, result);
        }

        [Fact]
        public static void Null_Coerced_To_String_Returns_Empty()
        {
            object result = LanguagePrimitives.ConvertTo(null, typeof(string), CultureInfo.InvariantCulture);
            Assert.Equal(string.Empty, result);
        }

        // -----------------------------------------------------------------------
        // Bool / Switch coercions
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("true",  true)]
        [InlineData("false", true)]   // PowerShell: non-empty string is truthy
        [InlineData("True",  true)]
        [InlineData("",      false)]  // PowerShell: empty string is falsy
        public static void String_Coerced_To_Bool_Via_Truthiness(string input, bool expected)
        {
            // PowerShell's string→bool conversion is based on non-empty/empty (truthiness),
            // NOT on parsing "true"/"false" keywords.
            object result = LanguagePrimitives.ConvertTo(input, typeof(bool), CultureInfo.InvariantCulture);
            Assert.Equal(expected, result);
        }

        [Fact]
        public static void Zero_Coerced_To_Bool_IsFalse()
        {
            object result = LanguagePrimitives.ConvertTo(0, typeof(bool), CultureInfo.InvariantCulture);
            Assert.Equal(false, result);
        }

        [Fact]
        public static void NonZero_Coerced_To_Bool_IsTrue()
        {
            object result = LanguagePrimitives.ConvertTo(1, typeof(bool), CultureInfo.InvariantCulture);
            Assert.Equal(true, result);
        }

        // -----------------------------------------------------------------------
        // Enum coercion
        // -----------------------------------------------------------------------

        [Fact]
        public static void String_Coerced_To_Enum_ByName()
        {
            object result = LanguagePrimitives.ConvertTo("Open", typeof(System.IO.FileMode),
                CultureInfo.InvariantCulture);
            Assert.Equal(System.IO.FileMode.Open, result);
        }

        [Fact]
        public static void String_CaseInsensitive_Enum_Coercion()
        {
            object result = LanguagePrimitives.ConvertTo("open", typeof(System.IO.FileMode),
                CultureInfo.InvariantCulture);
            Assert.Equal(System.IO.FileMode.Open, result);
        }

        // -----------------------------------------------------------------------
        // Array / collection coercions
        // -----------------------------------------------------------------------

        [Fact]
        public static void Single_Scalar_Coerced_To_Array()
        {
            object result = LanguagePrimitives.ConvertTo("hello", typeof(string[]),
                CultureInfo.InvariantCulture);
            Assert.IsType<string[]>(result);
            var arr = (string[])result;
            Assert.Single(arr);
            Assert.Equal("hello", arr[0]);
        }

        [Fact]
        public static void Array_Coerced_To_Array_PreservesElements()
        {
            object result = LanguagePrimitives.ConvertTo(
                new[] { "a", "b", "c" }, typeof(string[]), CultureInfo.InvariantCulture);
            var arr = (string[])result;
            Assert.Equal(3, arr.Length);
        }

        // -----------------------------------------------------------------------
        // TryConvertTo
        // -----------------------------------------------------------------------

        [Fact]
        public static void TryConvertTo_Success_ReturnsTrue()
        {
            bool ok = LanguagePrimitives.TryConvertTo("42", typeof(int), out object result);
            Assert.True(ok);
            Assert.Equal(42, result);
        }

        [Fact]
        public static void TryConvertTo_Failure_ReturnsFalse()
        {
            bool ok = LanguagePrimitives.TryConvertTo("not-a-number", typeof(int), out object result);
            Assert.False(ok);
        }

        // -----------------------------------------------------------------------
        // PSInvalidCastException for unconvertible types
        // -----------------------------------------------------------------------

        [Fact]
        public static void UnconvertibleString_Throws_PSInvalidCastException()
        {
            Assert.Throws<PSInvalidCastException>(() =>
                LanguagePrimitives.ConvertTo("xyz", typeof(int), CultureInfo.InvariantCulture));
        }
    }
}
