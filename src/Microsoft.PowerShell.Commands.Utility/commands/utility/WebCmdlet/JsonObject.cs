// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// JsonObject class.
    /// </summary>
    [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly")]
    public static class JsonObject
    {
        private const int DefaultMemberSetCapacity = 32;

        private class DuplicateMemberSet : HashSet<string>
        {
            public DuplicateMemberSet(int capacity) : base(capacity, StringComparer.CurrentCultureIgnoreCase)
            {
            }
        }

        /// <summary>
        /// Convert a Json string back to an object of type PSObject.
        /// </summary>
        /// <param name="input">The json text to convert.</param>
        /// <param name="error">An error record if the conversion failed.</param>
        /// <returns>A PSObject.</returns>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly")]
        public static object ConvertFromJson(string input, out ErrorRecord error)
        {
            return ConvertFromJson(input, returnHashTable: false, out error);
        }

        /// <summary>
        /// Convert a Json string back to an object of type PSObject or HashTable depending on parameter <paramref name="returnHashTable"/>.
        /// </summary>
        /// <param name="input">The json text to convert.</param>
        /// <param name="returnHashTable">True if the result should be returned as a HashTable instead of a PSObject.</param>
        /// <param name="error">An error record if the conversion failed.</param>
        /// <returns>A PSObject or a HashTable if the <paramref name="returnHashTable"/> parameter is true.</returns>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly")]
        public static object ConvertFromJson(string input, bool returnHashTable, out ErrorRecord error)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var memberTracker = new DuplicateMemberSet(DefaultMemberSetCapacity);
            error = null;
            object obj;
            try
            {
                // JsonConvert.DeserializeObject does not throw an exception when an invalid Json array is passed.
                // This issue is being tracked by https://github.com/JamesNK/Newtonsoft.Json/issues/1321.
                // To work around this, we need to identify when input is a Json array, and then try to parse it via JArray.Parse().

                // If input starts with '[' (ignoring white spaces).
                if (Regex.Match(input, @"^\s*\[").Success)
                {
                    // JArray.Parse() will throw a JsonException if the array is invalid.
                    // This will be caught by the catch block below, and then throw an
                    // ArgumentException - this is done to have same behavior as the JavaScriptSerializer.
                    JArray.Parse(input);

                    // Please note that if the Json array is valid, we don't do anything,
                    // we just continue the deserialization.
                }

                obj = JsonConvert.DeserializeObject(
                    input,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.None,
                        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                        MaxDepth = 1024
                    });

                // JObject is a IDictionary
                if (obj is JObject dictionary)
                {
                    obj = returnHashTable ?
                              PopulateHashTableFromJDictionary(dictionary, out error) :
                              PopulateFromJDictionary(dictionary, memberTracker, out error);
                }
                else
                {
                    // JArray is a collection
                    if (obj is JArray list)
                    {
                        obj = returnHashTable ?
                                  PopulateHashTableFromJArray(list, out error) :
                                  PopulateFromJArray(list, out error);
                    }
                }
            }
            catch (JsonException je)
            {
                var msg = string.Format(CultureInfo.CurrentCulture, WebCmdletStrings.JsonDeserializationFailed, je.Message);

                // the same as JavaScriptSerializer does
                throw new ArgumentException(msg, je);
            }

            return obj;
        }

        // This function is a clone of PopulateFromDictionary using JObject as an input.
        private static PSObject PopulateFromJDictionary(JObject entries, DuplicateMemberSet memberTracker, out ErrorRecord error)
        {
            error = null;
            var result = new PSObject(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Key))
                {
                    var errorMsg = string.Format(CultureInfo.InvariantCulture, WebCmdletStrings.EmptyKeyInJsonString);
                    error = new ErrorRecord(
                        new InvalidOperationException(errorMsg),
                        "EmptyKeyInJsonString",
                        ErrorCategory.InvalidOperation,
                        null);
                    return null;
                }

                // Case sensitive duplicates should normally not occur since JsonConvert.DeserializeObject
                // does not throw when encountering duplicates and just uses the last entry.
                if (memberTracker.TryGetValue(entry.Key, out var maybePropertyName)
                    && string.Compare(entry.Key, maybePropertyName, StringComparison.CurrentCulture) == 0)
                {
                    var errorMsg = string.Format(CultureInfo.InvariantCulture, WebCmdletStrings.DuplicateKeysInJsonString, entry.Key);
                    error = new ErrorRecord(
                        new InvalidOperationException(errorMsg),
                        "DuplicateKeysInJsonString",
                        ErrorCategory.InvalidOperation,
                        null);
                    return null;
                }

                // Compare case insensitive to tell the user to use the -AsHashTable option instead.
                // This is because PSObject cannot have keys with different casing.
                if (memberTracker.TryGetValue(entry.Key, out var propertyName))
                {
                    var errorMsg = string.Format(CultureInfo.InvariantCulture, WebCmdletStrings.KeysWithDifferentCasingInJsonString, propertyName, entry.Key);
                    error = new ErrorRecord(
                        new InvalidOperationException(errorMsg),
                        "KeysWithDifferentCasingInJsonString",
                        ErrorCategory.InvalidOperation,
                        null);
                    return null;
                }

                // Array
                if (entry.Value is JArray list)
                {
                    ICollection<object> listResult = PopulateFromJArray(list, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result.Properties.Add(new PSNoteProperty(entry.Key, listResult));
                }
                else if (entry.Value is JObject dic)
                {
                    // Dictionary
                    PSObject dicResult = PopulateFromJDictionary(dic, new DuplicateMemberSet(dic.Count), out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result.Properties.Add(new PSNoteProperty(entry.Key, dicResult));
                }
                else
                {
                    // Value
                    JValue theValue = (JValue)entry.Value;
                    result.Properties.Add(new PSNoteProperty(entry.Key, theValue.Value));
                }

                memberTracker.Add(entry.Key);
            }

            return result;
        }

        // This function is a clone of PopulateFromList using JArray as input.
        private static ICollection<object> PopulateFromJArray(JArray list, out ErrorRecord error)
        {
            error = null;
            var result = new object[list.Count];

            for (var index = 0; index < list.Count; index++)
            {
                var element = list[index];
                if (element is JArray subList)
                {
                    // Array
                    ICollection<object> listResult = PopulateFromJArray(subList, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result[index] = listResult;
                }
                else if (element is JObject dic)
                {
                    // Dictionary
                    PSObject dicResult = PopulateFromJDictionary(dic, new DuplicateMemberSet(dic.Count),  out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result[index] = dicResult;
                }
                else
                {
                    // Value
                    result[index] = ((JValue)element).Value;
                }
            }

            return result;
        }

        // This function is a clone of PopulateFromDictionary using JObject as an input.
        private static Hashtable PopulateHashTableFromJDictionary(JObject entries, out ErrorRecord error)
        {
            error = null;
            Hashtable result = new Hashtable();
            foreach (var entry in entries)
            {
                // Case sensitive duplicates should normally not occur since JsonConvert.DeserializeObject
                // does not throw when encountering duplicates and just uses the last entry.
                if (result.ContainsKey(entry.Key))
                {
                    string errorMsg = string.Format(CultureInfo.InvariantCulture, WebCmdletStrings.DuplicateKeysInJsonString, entry.Key);
                    error = new ErrorRecord(
                        new InvalidOperationException(errorMsg),
                        "DuplicateKeysInJsonString",
                        ErrorCategory.InvalidOperation,
                        null);
                    return null;
                }

                if (entry.Value is JArray list)
                {
                    // Array
                    ICollection<object> listResult = PopulateHashTableFromJArray(list, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result.Add(entry.Key, listResult);
                }
                else if (entry.Value is JObject dic)
                {
                    // Dictionary
                    Hashtable dicResult = PopulateHashTableFromJDictionary(dic, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result.Add(entry.Key, dicResult);
                }
                else
                {
                    // Value
                    JValue theValue = entry.Value as JValue;
                    result.Add(entry.Key, theValue.Value);
                }
            }

            return result;
        }

        // This function is a clone of PopulateFromList using JArray as input.
        private static ICollection<object> PopulateHashTableFromJArray(JArray list, out ErrorRecord error)
        {
            error = null;
            var result = new object[list.Count];

            for (var index = 0; index < list.Count; index++)
            {
                var element = list[index];

                if (element is JArray subList)
                {
                    // Array
                    ICollection<object> listResult = PopulateHashTableFromJArray(subList, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result[index] = listResult;
                }
                else if (element is JObject dic)
                {
                    // Dictionary
                    Hashtable dicResult = PopulateHashTableFromJDictionary(dic, out error);
                    if (error != null)
                    {
                        return null;
                    }

                    result[index] = dicResult;
                }
                else
                {
                    // Value
                    result[index] = ((JValue)element).Value;
                }
            }

            return result;
        }
    }
}
