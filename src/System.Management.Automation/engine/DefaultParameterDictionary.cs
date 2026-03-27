// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Management.Automation.Language;
using System.Text;

namespace System.Management.Automation;

/// <summary>
/// A versionable hashtable, so the caching of UserInput -> ParameterBindingResult will work.
/// </summary>
[SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable", Justification = "DefaultParameterDictionary will only be used for $PSDefaultParameterValues.")]
public sealed class DefaultParameterDictionary : Hashtable
{
    private bool _isChanged;

    /// <summary>
    /// Check to see if the hashtable has been changed since last check.
    /// </summary>
    /// <returns>True for changed; false for not changed.</returns>
    public bool ChangeSinceLastCheck()
    {
        bool ret = _isChanged;
        _isChanged = false;
        return ret;
    }

    #region Constructor

    /// <summary>
    /// Default constructor.
    /// </summary>
    public DefaultParameterDictionary()
        : base(StringComparer.OrdinalIgnoreCase)
    {
        _isChanged = true;
    }

    /// <summary>
    /// Constructor takes a hash table.
    /// </summary>
    /// <remarks>
    /// Check for the keys' formats and make it versionable
    /// </remarks>
    /// <param name="dictionary">A hashtable instance.</param>
    public DefaultParameterDictionary(IDictionary dictionary)
        : this()
    {
        if (dictionary == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(dictionary));
        }
        // Contains keys that are in bad format. For every bad format key, we should write out a warning message
        // the first time we encounter it, and remove it from the $PSDefaultParameterValues
        var keysInBadFormat = new List<object>();

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string entryKey)
            {
                string key = entryKey.Trim();
                string cmdletName = null;
                string parameterName = null;
                bool isSpecialKey = false; // The key is 'Disabled'

                // The key is not with valid format
                if (!CheckKeyIsValid(key, ref cmdletName, ref parameterName))
                {
                    isSpecialKey = key.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
                    if (!isSpecialKey)
                    {
                        keysInBadFormat.Add(entryKey);
                        continue;
                    }
                }

                Diagnostics.Assert(isSpecialKey || (cmdletName != null && parameterName != null), "The cmdletName and parameterName should be set in CheckKeyIsValid");
                if (keysInBadFormat.Count == 0 && !base.ContainsKey(key))
                {
                    base.Add(key, entry.Value);
                }
            }
            else
            {
                keysInBadFormat.Add(entry.Key);
            }
        }

        var keysInError = new StringBuilder();
        foreach (object badFormatKey in keysInBadFormat)
        {
            keysInError.Append(badFormatKey.ToString() + ", ");
        }

        if (keysInError.Length > 0)
        {
            keysInError.Remove(keysInError.Length - 2, 2);
            string resourceString = keysInBadFormat.Count > 1
                                        ? ParameterBinderStrings.MultipleKeysInBadFormat
                                        : ParameterBinderStrings.SingleKeyInBadFormat;
            throw PSTraceSource.NewInvalidOperationException(resourceString, keysInError);
        }
    }

    #endregion Constructor

    /// <summary>
    /// Override Contains.
    /// </summary>
    public override bool Contains(object key)
    {
        return this.ContainsKey(key);
    }

    /// <summary>
    /// Override ContainsKey.
    /// </summary>
    public override bool ContainsKey(object key)
    {
        if (key == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(key));
        }

        if (key is not string strKey)
        {
            return false;
        }

        string keyAfterTrim = strKey.Trim();
        return base.ContainsKey(keyAfterTrim);
    }

    /// <summary>
    /// Override the Add to check for key's format and make it versionable.
    /// </summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public override void Add(object key, object value)
    {
        AddImpl(key, value, isSelfIndexing: false);
    }

    /// <summary>
    /// Actual implementation for Add.
    /// </summary>
    private void AddImpl(object key, object value, bool isSelfIndexing)
    {
        if (key == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(key));
        }

        if (key is not string strKey)
        {
            throw PSTraceSource.NewArgumentException(nameof(key), ParameterBinderStrings.StringValueKeyExpected, key, key.GetType().FullName);
        }

        string keyAfterTrim = strKey.Trim();
        string cmdletName = null;
        string parameterName = null;

        if (base.ContainsKey(keyAfterTrim))
        {
            if (isSelfIndexing)
            {
                _isChanged = true;
                base[keyAfterTrim] = value;
                return;
            }

            throw PSTraceSource.NewArgumentException(nameof(key), ParameterBinderStrings.KeyAlreadyAdded, key);
        }

        if (!CheckKeyIsValid(keyAfterTrim, ref cmdletName, ref parameterName))
        {
            // The key is not in valid format
            if (!keyAfterTrim.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw PSTraceSource.NewInvalidOperationException(ParameterBinderStrings.SingleKeyInBadFormat, key);
            }
        }

        _isChanged = true;
        base.Add(keyAfterTrim, value);
    }

    /// <summary>
    /// Override the indexing to check for key's format and make it versionable.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public override object this[object key]
    {
        get
        {
            if (key == null)
            {
                throw PSTraceSource.NewArgumentNullException(nameof(key));
            }

            if (key is not string strKey)
            {
                return null;
            }

            string keyAfterTrim = strKey.Trim();
            return base[keyAfterTrim];
        }

        set
        {
            AddImpl(key, value, isSelfIndexing: true);
        }
    }

    /// <summary>
    /// Override the Remove to make it versionable.
    /// </summary>
    /// <param name="key">Key.</param>
    public override void Remove(object key)
    {
        if (key == null)
        {
            throw PSTraceSource.NewArgumentNullException(nameof(key));
        }

        if (key is not string strKey)
        {
            return;
        }

        string keyAfterTrim = strKey.Trim();
        if (base.ContainsKey(keyAfterTrim))
        {
            base.Remove(keyAfterTrim);
            _isChanged = true;
        }
    }

    /// <summary>
    /// Override the Clear to make it versionable.
    /// </summary>
    public override void Clear()
    {
        base.Clear();
        _isChanged = true;
    }

    #region KeyValidation

    /// <summary>
    /// Check if the key is in valid format. If it is, get the cmdlet name and parameter name.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cmdletName"></param>
    /// <param name="parameterName"></param>
    /// <returns>Return true if the key is valid, false if not.</returns>
    internal static bool CheckKeyIsValid(string key, ref string cmdletName, ref string parameterName)
    {
        if (key == string.Empty)
        {
            return false;
        }

        // The index returned should point to the separator or a character that is before the separator
        int index = GetValueToken(0, key, ref cmdletName, true);
        if (index == -1)
        {
            return false;
        }

        // The index returned should point to the first non-whitespace character, and it should be the separator
        index = SkipWhiteSpace(index, key);
        if (index == -1 || key[index] != ':')
        {
            return false;
        }

        // The index returned should point to the first non-whitespace character after the separator
        index = SkipWhiteSpace(index + 1, key);
        if (index == -1)
        {
            return false;
        }

        // The index returned should point to the last character in key
        index = GetValueToken(index, key, ref parameterName, false);
        if (index == -1 || index != key.Length)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Get the cmdlet name and the parameter name.
    /// </summary>
    /// <param name="index">Point to a non-whitespace character.</param>
    /// <param name="key">The string to iterate over.</param>
    /// <param name="name"></param>
    /// <param name="getCmdletName">Specify whether to get the cmdlet name or parameter name.</param>
    /// <returns>
    /// For cmdletName:
    /// When the name is enclosed by quotes, the index returned should be the index of the character right after the second quote;
    /// When the name is not enclosed by quotes, the index returned should be the index of the separator;
    ///
    /// For parameterName:
    /// When the name is enclosed by quotes, the index returned should be the index of the second quote plus 1 (the length of the key if the key is in a valid format);
    /// When the name is not enclosed by quotes, the index returned should be the length of the key.
    /// </returns>
    private static int GetValueToken(int index, string key, ref string name, bool getCmdletName)
    {
        char quoteChar = '\0';
        if (key[index].IsSingleQuote() || key[index].IsDoubleQuote())
        {
            quoteChar = key[index];
            index++;
        }

        StringBuilder builder = new StringBuilder(string.Empty);
        for (; index < key.Length; index++)
        {
            if (quoteChar != '\0')
            {
                if ((quoteChar.IsSingleQuote() && key[index].IsSingleQuote()) ||
                    (quoteChar.IsDoubleQuote() && key[index].IsDoubleQuote()))
                {
                    name = builder.ToString().Trim();
                    // Make the index point to the character right after the quote
                    return name.Length == 0 ? -1 : index + 1;
                }

                builder.Append(key[index]);
                continue;
            }

            if (getCmdletName)
            {
                if (key[index] != ':')
                {
                    builder.Append(key[index]);
                    continue;
                }

                name = builder.ToString().Trim();
                return name.Length == 0 ? -1 : index;
            }
            else
            {
                builder.Append(key[index]);
            }
        }

        if (!getCmdletName && quoteChar == '\0')
        {
            name = builder.ToString().Trim();
            Diagnostics.Assert(name.Length > 0, "name should not be empty at this point");
            return index;
        }

        return -1;
    }

    /// <summary>
    /// Skip whitespace characters.
    /// </summary>
    /// <param name="index">Start index.</param>
    /// <param name="key">The string to iterate over.</param>
    /// <returns>
    /// Return -1 if we reach the end of the key, otherwise return the index of the first
    /// non-whitespace character we encounter.
    /// </returns>
    private static int SkipWhiteSpace(int index, string key)
    {
        for (; index < key.Length; index++)
        {
            if (key[index].IsWhitespace() || key[index] == '\r' || key[index] == '\n')
                continue;
            return index;
        }

        return -1;
    }

    #endregion KeyValidation
}
