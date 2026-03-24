// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Management.Automation;
using Xunit;

namespace PSTests.Parallel
{
    // -----------------------------------------------------------------------
    // ValidateLengthAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateLengthAttributeTests
    {
        [Fact]
        public static void Constructor_StoresMinAndMaxLength()
        {
            var attr = new ValidateLengthAttribute(2, 5);
            Assert.Equal(2, attr.MinLength);
            Assert.Equal(5, attr.MaxLength);
        }

        [Fact]
        public static void Constructor_ThrowsOnNegativeMin()
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new ValidateLengthAttribute(-1, 5));
        }

        [Fact]
        public static void Constructor_ThrowsOnZeroMax()
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new ValidateLengthAttribute(0, 0));
        }

        [Fact]
        public static void Constructor_ThrowsWhenMaxLessThanMin()
        {
            Assert.Throws<ValidationMetadataException>(() => new ValidateLengthAttribute(5, 2));
        }

        [Fact]
        public static void Validate_AcceptsStringWithinBounds()
        {
            var attr = new ValidateLengthAttribute(2, 5);
            attr.InternalValidate("hi", null);   // length 2, no throw
        }

        [Fact]
        public static void Validate_ThrowsForStringTooShort()
        {
            var attr = new ValidateLengthAttribute(2, 5);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate("a", null));
        }

        [Fact]
        public static void Validate_ThrowsForStringTooLong()
        {
            var attr = new ValidateLengthAttribute(2, 5);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate("123456", null));
        }

        [Fact]
        public static void Validate_ThrowsForNullArgument()
        {
            var attr = new ValidateLengthAttribute(2, 5);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(null, null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidateRangeAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateRangeAttributeTests
    {
        [Fact]
        public static void Constructor_StoresMinAndMaxRange()
        {
            var attr = new ValidateRangeAttribute(1, 10);
            Assert.Equal(1, attr.MinRange);
            Assert.Equal(10, attr.MaxRange);
        }

        [Fact]
        public static void Constructor_ThrowsWhenMaxLessThanMin()
        {
            Assert.Throws<ValidationMetadataException>(() => new ValidateRangeAttribute(10, 1));
        }

        [Fact]
        public static void Constructor_ThrowsOnNullMin()
        {
            Assert.ThrowsAny<ArgumentNullException>(() => new ValidateRangeAttribute(null, 5));
        }

        [Fact]
        public static void Constructor_KindPositive_StoresKind()
        {
            var attr = new ValidateRangeAttribute(ValidateRangeKind.Positive);
            Assert.Equal(ValidateRangeKind.Positive, attr.RangeKind);
        }

        [Fact]
        public static void Validate_AcceptsValueWithinRange()
        {
            var attr = new ValidateRangeAttribute(1, 10);
            attr.InternalValidate(5, null);   // no throw
        }

        [Fact]
        public static void Validate_ThrowsForValueBelowMin()
        {
            var attr = new ValidateRangeAttribute(1, 10);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(0, null));
        }

        [Fact]
        public static void Validate_ThrowsForValueAboveMax()
        {
            var attr = new ValidateRangeAttribute(1, 10);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(11, null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidateSetAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateSetAttributeTests
    {
        [Fact]
        public static void Constructor_StoresValidValues()
        {
            var attr = new ValidateSetAttribute("Red", "Green", "Blue");
            Assert.Equal(3, attr.ValidValues.Count);
            Assert.Contains("Red", attr.ValidValues);
        }

        [Fact]
        public static void Constructor_ThrowsOnNullValidValues()
        {
            Assert.ThrowsAny<ArgumentNullException>(() => new ValidateSetAttribute((string[])null));
        }

        [Fact]
        public static void Constructor_ThrowsOnEmptyValidValues()
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new ValidateSetAttribute(Array.Empty<string>()));
        }

        [Fact]
        public static void Validate_AcceptsValueInSet()
        {
            var attr = new ValidateSetAttribute("Red", "Green", "Blue");
            attr.InternalValidate("Red", null);   // no throw
        }

        [Fact]
        public static void Validate_AcceptsValueCaseInsensitivelyByDefault()
        {
            var attr = new ValidateSetAttribute("Red", "Green", "Blue");
            attr.InternalValidate("red", null);   // IgnoreCase=true by default, no throw
        }

        [Fact]
        public static void Validate_ThrowsForValueNotInSet()
        {
            var attr = new ValidateSetAttribute("Red", "Green", "Blue");
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate("Yellow", null));
        }

        [Fact]
        public static void Validate_ThrowsWhenCaseSensitiveAndCaseMismatched()
        {
            var attr = new ValidateSetAttribute("Red") { IgnoreCase = false };
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate("red", null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidatePatternAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidatePatternAttributeTests
    {
        [Fact]
        public static void Constructor_StoresRegexPattern()
        {
            var attr = new ValidatePatternAttribute(@"^\d+$");
            Assert.Equal(@"^\d+$", attr.RegexPattern);
        }

        [Fact]
        public static void Constructor_ThrowsOnNullOrEmptyPattern()
        {
            Assert.ThrowsAny<ArgumentException>(() => new ValidatePatternAttribute(null));
            Assert.ThrowsAny<ArgumentException>(() => new ValidatePatternAttribute(string.Empty));
        }

        [Fact]
        public static void Validate_AcceptsMatchingString()
        {
            var attr = new ValidatePatternAttribute(@"^\d+$");
            attr.InternalValidate("12345", null);   // no throw
        }

        [Fact]
        public static void Validate_ThrowsForNonMatchingString()
        {
            var attr = new ValidatePatternAttribute(@"^\d+$");
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate("abc", null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidateCountAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateCountAttributeTests
    {
        [Fact]
        public static void Constructor_StoresMinAndMaxLength()
        {
            var attr = new ValidateCountAttribute(2, 4);
            Assert.Equal(2, attr.MinLength);
            Assert.Equal(4, attr.MaxLength);
        }

        [Fact]
        public static void Constructor_ThrowsWhenMaxLessThanMin()
        {
            Assert.Throws<ValidationMetadataException>(() => new ValidateCountAttribute(4, 2));
        }

        [Fact]
        public static void Validate_AcceptsArrayWithCountInRange()
        {
            var attr = new ValidateCountAttribute(2, 4);
            attr.InternalValidate(new[] { 1, 2, 3 }, null);   // count 3, no throw
        }

        [Fact]
        public static void Validate_ThrowsForArrayCountBelowMin()
        {
            var attr = new ValidateCountAttribute(2, 4);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(new[] { 1 }, null));
        }

        [Fact]
        public static void Validate_ThrowsForNonCollectionArgument()
        {
            var attr = new ValidateCountAttribute(1, 3);
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(42, null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidateNotNullAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateNotNullAttributeTests
    {
        [Fact]
        public static void Validate_AcceptsNonNullObject()
        {
            var attr = new ValidateNotNullAttribute();
            attr.InternalValidate("hello", null);   // no throw
        }

        [Fact]
        public static void Validate_ThrowsForNullValue()
        {
            var attr = new ValidateNotNullAttribute();
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(null, null));
        }

        [Fact]
        public static void Validate_AcceptsEmptyString()
        {
            // Empty string is not null — ValidateNotNull passes for string.Empty
            var attr = new ValidateNotNullAttribute();
            attr.InternalValidate(string.Empty, null);   // no throw
        }

        [Fact]
        public static void Validate_AcceptsArrayWithNoNullElements()
        {
            var attr = new ValidateNotNullAttribute();
            attr.InternalValidate(new string[] { "a", "b" }, null);   // no throw
        }

        [Fact]
        public static void Validate_ThrowsForArrayContainingNullElement()
        {
            var attr = new ValidateNotNullAttribute();
            Assert.Throws<ValidationMetadataException>(() =>
                attr.InternalValidate(new object[] { 1, null }, null));
        }
    }

    // -----------------------------------------------------------------------
    // ValidateNotNullOrEmptyAttribute tests
    // -----------------------------------------------------------------------
    public static class ValidateNotNullOrEmptyAttributeTests
    {
        [Fact]
        public static void Validate_AcceptsNonEmptyString()
        {
            var attr = new ValidateNotNullOrEmptyAttribute();
            attr.InternalValidate("hello", null);   // no throw
        }

        [Fact]
        public static void Validate_ThrowsForNullValue()
        {
            var attr = new ValidateNotNullOrEmptyAttribute();
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(null, null));
        }

        [Fact]
        public static void Validate_ThrowsForEmptyString()
        {
            var attr = new ValidateNotNullOrEmptyAttribute();
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(string.Empty, null));
        }

        [Fact]
        public static void Validate_ThrowsForEmptyArray()
        {
            var attr = new ValidateNotNullOrEmptyAttribute();
            Assert.Throws<ValidationMetadataException>(() => attr.InternalValidate(Array.Empty<string>(), null));
        }
    }
}
