// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Text;

namespace System.Management.Automation.Internal
{
    /// <summary>
    /// Classifies source spans so formatters never split or reinterpret terminal payloads.
    /// </summary>
    internal enum FormattedTextTokenKind
    {
        Text,
        LineBreak,
        Sixel,
        InvalidSixel,
        OpaqueDcs,
    }

    /// <summary>
    /// Refers to one contiguous span of the original formatted string without copying it.
    /// </summary>
    internal readonly struct FormattedTextToken
    {
        internal FormattedTextToken(int start, int length, FormattedTextTokenKind kind, SixelMetadata metadata = default)
        {
            Start = start;
            Length = length;
            Kind = kind;
            SixelMetadata = metadata;
        }

        internal int Start { get; }

        internal int Length { get; }

        internal FormattedTextTokenKind Kind { get; }

        internal SixelMetadata SixelMetadata { get; }
    }

    /// <summary>
    /// Owns the parsed, source-preserving representation used by image-aware formatting policy.
    /// </summary>
    internal sealed class FormattedText
    {
        internal const string SixelFallback = "[sixel]";

        private const char Escape = '\x1b';
        private const char C1DeviceControlString = '\x90';
        private const char C1StringTerminator = '\x9c';

        private readonly IReadOnlyList<FormattedTextToken> _tokens;

        private FormattedText(string source, IReadOnlyList<FormattedTextToken> tokens, bool hasImages, bool hasUnsupportedImageCandidates)
        {
            Source = source;
            _tokens = tokens;
            HasImages = hasImages;
            HasUnsupportedImageCandidates = hasUnsupportedImageCandidates;
        }

        internal string Source { get; }

        internal IReadOnlyList<FormattedTextToken> Tokens => _tokens;

        internal bool HasImages { get; }

        internal bool HasUnsupportedImageCandidates { get; }

        /// <summary>
        /// Performs the allocation-free prefix check used to keep ordinary text on the legacy formatting path.
        /// </summary>
        internal static bool MightContainImage(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return source.Contains("\x1bP", StringComparison.Ordinal) || source.Contains(C1DeviceControlString);
        }

        /// <summary>
        /// Tokenizes text, line breaks, and complete DCS candidates while preserving their original source spans.
        /// </summary>
        internal static FormattedText Parse(string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            var tokens = new List<FormattedTextToken>();
            bool hasImages = false;
            bool hasUnsupportedCandidates = false;
            int textStart = 0;
            int index = 0;

            while (index < source.Length)
            {
                if (SixelParser.TryParseCandidate(source, index, out SixelParseResult result))
                {
                    AddTextToken(tokens, textStart, index);
                    FormattedTextTokenKind kind = GetDcsTokenKind(result.Status);
                    tokens.Add(new FormattedTextToken(result.Start, result.Length, kind, result.Metadata));
                    hasImages |= kind == FormattedTextTokenKind.Sixel;
                    hasUnsupportedCandidates |= kind == FormattedTextTokenKind.InvalidSixel;
                    index += result.Length;
                    textStart = index;
                    continue;
                }

                if (source[index] == C1DeviceControlString)
                {
                    AddTextToken(tokens, textStart, index);
                    int length = FindC1CandidateLength(source, index);
                    tokens.Add(new FormattedTextToken(index, length, FormattedTextTokenKind.InvalidSixel));
                    hasUnsupportedCandidates = true;
                    index += length;
                    textStart = index;
                    continue;
                }

                if (source[index] is '\r' or '\n')
                {
                    AddTextToken(tokens, textStart, index);
                    int length = source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n' ? 2 : 1;
                    tokens.Add(new FormattedTextToken(index, length, FormattedTextTokenKind.LineBreak));
                    index += length;
                    textStart = index;
                    continue;
                }

                index++;
            }

            AddTextToken(tokens, textStart, source.Length);
            return new FormattedText(source, tokens, hasImages, hasUnsupportedCandidates);
        }

        /// <summary>
        /// Replaces image candidates only when needed, otherwise returning the original string instance.
        /// </summary>
        internal static string GetFallbackIfNeeded(string source)
        {
            if (string.IsNullOrEmpty(source) || !MightContainImage(source))
            {
                return source;
            }

            return Parse(source).ToFallbackString();
        }

        /// <summary>
        /// Projects image candidates to the formatter fallback while retaining all unrelated source content.
        /// </summary>
        internal string ToFallbackString()
        {
            if (!HasImages && !HasUnsupportedImageCandidates)
            {
                return Source;
            }

            var builder = new StringBuilder(Source.Length);
            foreach (FormattedTextToken token in _tokens)
            {
                if (token.Kind is FormattedTextTokenKind.Sixel or FormattedTextTokenKind.InvalidSixel)
                {
                    builder.Append(SixelFallback);
                }
                else
                {
                    builder.Append(Source, token.Start, token.Length);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Removes complete image spans before applying the existing plain-text escape handling.
        /// </summary>
        internal string ToPlainTextString()
        {
            var builder = new StringBuilder(Source.Length);
            foreach (FormattedTextToken token in _tokens)
            {
                if (token.Kind is not (FormattedTextTokenKind.Sixel or FormattedTextTokenKind.InvalidSixel))
                {
                    builder.Append(Source, token.Start, token.Length);
                }
            }

            return ValueStringDecorated.AnsiRegex.Replace(builder.ToString(), string.Empty);
        }

        private static FormattedTextTokenKind GetDcsTokenKind(SixelParseStatus status)
        {
            return status switch
            {
                SixelParseStatus.Valid => FormattedTextTokenKind.Sixel,
                SixelParseStatus.NotSixel => FormattedTextTokenKind.OpaqueDcs,
                _ => FormattedTextTokenKind.InvalidSixel,
            };
        }

        private static void AddTextToken(List<FormattedTextToken> tokens, int start, int end)
        {
            if (end > start)
            {
                tokens.Add(new FormattedTextToken(start, end - start, FormattedTextTokenKind.Text));
            }
        }

        private static int FindC1CandidateLength(string source, int start)
        {
            int index = start + 1;
            while (index < source.Length)
            {
                if (source[index] == C1StringTerminator)
                {
                    return index - start + 1;
                }

                if (source[index] == Escape)
                {
                    if (index + 1 < source.Length && source[index + 1] == '\\')
                    {
                        return index - start + 2;
                    }

                    return index - start;
                }

                index++;
            }

            return source.Length - start;
        }
    }
}
