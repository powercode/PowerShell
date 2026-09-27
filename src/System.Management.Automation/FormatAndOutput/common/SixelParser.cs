// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;

namespace System.Management.Automation.Internal
{
    /// <summary>
    /// Describes whether a DCS candidate is a sixel image and whether managed rendering can use it.
    /// </summary>
    internal enum SixelParseStatus
    {
        NotSixel,
        Valid,
        Unsupported,
        Invalid,
    }

    /// <summary>
    /// Preserves the distinct dimensions needed to lay out and safely render one sixel image.
    /// </summary>
    internal readonly struct SixelMetadata
    {
        internal SixelMetadata(
            long? p1,
            long? p2,
            long? p3,
            long traversalWidth,
            long paintedHeight,
            long bandHeight,
            long cursorTravelHeight,
            long? declaredWidth,
            long? declaredHeight,
            int? pan,
            int? pad)
        {
            P1 = p1;
            P2 = p2;
            P3 = p3;
            TraversalWidth = traversalWidth;
            PaintedHeight = paintedHeight;
            BandHeight = bandHeight;
            CursorTravelHeight = cursorTravelHeight;
            DeclaredWidth = declaredWidth;
            DeclaredHeight = declaredHeight;
            Pan = pan;
            Pad = pad;
        }

        internal long? P1 { get; }

        internal long? P2 { get; }

        internal long? P3 { get; }

        internal long TraversalWidth { get; }

        internal long PaintedHeight { get; }

        internal long BandHeight { get; }

        internal long CursorTravelHeight { get; }

        internal long? DeclaredWidth { get; }

        internal long? DeclaredHeight { get; }

        internal int? Pan { get; }

        internal int? Pad { get; }
    }

    /// <summary>
    /// Identifies one complete source candidate without copying or rewriting its payload.
    /// </summary>
    internal readonly struct SixelParseResult
    {
        internal SixelParseResult(int start, int length, SixelParseStatus status, SixelMetadata metadata)
        {
            Start = start;
            Length = length;
            Status = status;
            Metadata = metadata;
        }

        internal int Start { get; }

        internal int Length { get; }

        internal SixelParseStatus Status { get; }

        internal SixelMetadata Metadata { get; }
    }

    /// <summary>
    /// Recognizes DCS boundaries and derives sixel extents without decoding pixels or expanding repeats.
    /// </summary>
    internal static class SixelParser
    {
        private const char Escape = '\x1b';
        private const char DeviceControlString = 'P';
        private const char StringTerminator = '\\';
        private const char Cancel = '\x18';
        private const char Substitute = '\x1a';

        /// <summary>
        /// Parses one DCS candidate at the requested source offset and reports its exact span and renderability metadata.
        /// </summary>
        internal static bool TryParseCandidate(string source, int start, out SixelParseResult result)
        {
            ArgumentNullException.ThrowIfNull(source);

            result = default;
            if (start < 0 || start > source.Length - 2 || source[start] != Escape || source[start + 1] != DeviceControlString)
            {
                return false;
            }

            int end = FindCandidateEnd(source, start + 2, out bool terminated, out bool aborted);
            int length = end - start;
            if (!terminated || aborted)
            {
                result = new SixelParseResult(start, length, SixelParseStatus.Invalid, default);
                return true;
            }

            int bodyStart = ParseHeader(
                source,
                start + 2,
                end - 2,
                out SixelParseStatus headerStatus,
                out long? p1,
                out long? p2,
                out long? p3);
            if (headerStatus == SixelParseStatus.NotSixel)
            {
                result = new SixelParseResult(start, length, headerStatus, default);
                return true;
            }

            if (headerStatus != SixelParseStatus.Valid)
            {
                result = new SixelParseResult(start, length, headerStatus, default);
                return true;
            }

            SixelParseStatus bodyStatus = ParseBody(source, bodyStart, end - 2, p1, p2, p3, out SixelMetadata metadata);
            result = new SixelParseResult(start, length, bodyStatus, metadata);
            return true;
        }

        private static int FindCandidateEnd(string source, int index, out bool terminated, out bool aborted)
        {
            terminated = false;
            aborted = false;

            while (index < source.Length)
            {
                char value = source[index];
                if (value is Cancel or Substitute)
                {
                    aborted = true;
                    return index + 1;
                }

                if (value == Escape)
                {
                    if (index + 1 < source.Length && source[index + 1] == StringTerminator)
                    {
                        terminated = true;
                        return index + 2;
                    }

                    return index;
                }

                index++;
            }

            return source.Length;
        }

        private static int ParseHeader(
            string source,
            int index,
            int contentEnd,
            out SixelParseStatus status,
            out long? p1,
            out long? p2,
            out long? p3)
        {
            p1 = null;
            p2 = null;
            p3 = null;
            var parameters = new long?[3];
            int parameterIndex = 0;
            int numberStart = index;
            long value = 0;

            while (index < contentEnd)
            {
                char character = source[index];
                if (character == 'q')
                {
                    if (index > numberStart)
                    {
                        parameters[parameterIndex] = value;
                    }

                    p1 = parameters[0];
                    p2 = parameters[1];
                    p3 = parameters[2];
                    status = SixelParseStatus.Valid;
                    return index + 1;
                }

                if (character == ';')
                {
                    if (index > numberStart)
                    {
                        parameters[parameterIndex] = value;
                    }

                    parameterIndex++;
                    if (parameterIndex == parameters.Length)
                    {
                        status = SixelParseStatus.Unsupported;
                        return index;
                    }

                    value = 0;
                    numberStart = index + 1;
                }
                else if (char.IsAsciiDigit(character))
                {
                    int digit = character - '0';
                    if (value > (long.MaxValue - digit) / 10)
                    {
                        status = SixelParseStatus.Invalid;
                        return index;
                    }

                    value = (value * 10) + digit;
                }
                else
                {
                    status = SixelParseStatus.NotSixel;
                    return index;
                }

                index++;
            }

            status = SixelParseStatus.NotSixel;
            return index;
        }

        private static SixelParseStatus ParseBody(
            string source,
            int index,
            int contentEnd,
            long? p1,
            long? p2,
            long? p3,
            out SixelMetadata metadata)
        {
            long horizontalPosition = 0;
            long bandTop = 0;
            long traversalWidth = 0;
            long paintedHeight = 0;
            long bandHeight = 0;
            long? declaredWidth = null;
            long? declaredHeight = null;
            int? pan = null;
            int? pad = null;
            bool unsupported = false;

            while (index < contentEnd)
            {
                char value = source[index++];
                if (value is >= '?' and <= '~')
                {
                    if (!ApplySixel(value, 1, bandTop, ref horizontalPosition, ref traversalWidth, ref paintedHeight, ref bandHeight))
                    {
                        return Invalid(out metadata);
                    }

                    continue;
                }

                switch (value)
                {
                    case '!':
                        if (!TryReadNumber(source, ref index, contentEnd, out long repeat) || index >= contentEnd || source[index] is < '?' or > '~')
                        {
                            return Invalid(out metadata);
                        }

                        unsupported |= repeat == 0;
                        if (!ApplySixel(source[index++], repeat, bandTop, ref horizontalPosition, ref traversalWidth, ref paintedHeight, ref bandHeight))
                        {
                            return Invalid(out metadata);
                        }

                        break;

                    case '$':
                        horizontalPosition = 0;
                        break;

                    case '-':
                        horizontalPosition = 0;
                        if (!TryAdd(bandTop, 6, out bandTop))
                        {
                            return Invalid(out metadata);
                        }

                        break;

                    case '#':
                        if (!TryReadCommandParameters(source, ref index, contentEnd, out long[] colorParameters)
                            || (colorParameters.Length != 1 && colorParameters.Length != 5)
                            || (colorParameters.Length == 5 && colorParameters[1] is not (1 or 2)))
                        {
                            return Invalid(out metadata);
                        }

                        break;

                    case '"':
                        if (!TryReadCommandParameters(source, ref index, contentEnd, out long[] rasterParameters)
                            || rasterParameters.Length is < 2 or > 4
                            || rasterParameters[0] > int.MaxValue
                            || rasterParameters[1] > int.MaxValue)
                        {
                            return Invalid(out metadata);
                        }

                        pan = (int)rasterParameters[0];
                        pad = (int)rasterParameters[1];
                        unsupported |= pan != 1 || pad != 1;
                        if (rasterParameters.Length >= 3)
                        {
                            declaredWidth = rasterParameters[2];
                        }

                        if (rasterParameters.Length == 4)
                        {
                            declaredHeight = rasterParameters[3];
                        }

                        break;

                    case '\r':
                    case '\n':
                        unsupported = true;
                        break;

                    default:
                        return Invalid(out metadata);
                }
            }

            long cursorTravelHeight;
            if (!TryAdd(bandTop, 6, out cursorTravelHeight))
            {
                return Invalid(out metadata);
            }

            metadata = new SixelMetadata(
                p1,
                p2,
                p3,
                traversalWidth,
                paintedHeight,
                bandHeight,
                cursorTravelHeight,
                declaredWidth,
                declaredHeight,
                pan,
                pad);
            return unsupported ? SixelParseStatus.Unsupported : SixelParseStatus.Valid;
        }

        private static bool ApplySixel(
            char value,
            long repeat,
            long bandTop,
            ref long horizontalPosition,
            ref long traversalWidth,
            ref long paintedHeight,
            ref long bandHeight)
        {
            if (!TryAdd(horizontalPosition, repeat, out horizontalPosition))
            {
                return false;
            }

            traversalWidth = Math.Max(traversalWidth, horizontalPosition);
            bandHeight = Math.Max(bandHeight, bandTop + 6);

            int mask = value - '?';
            if (mask == 0)
            {
                return true;
            }

            int highestBit = 0;
            while ((mask >>= 1) != 0)
            {
                highestBit++;
            }

            return TryAdd(bandTop, highestBit + 1, out long bottom) && SetMaximum(ref paintedHeight, bottom);
        }

        private static bool TryReadCommandParameters(string source, ref int index, int contentEnd, out long[] parameters)
        {
            var values = new List<long>();
            while (index < contentEnd)
            {
                if (!TryReadNumber(source, ref index, contentEnd, out long value))
                {
                    parameters = Array.Empty<long>();
                    return false;
                }

                values.Add(value);
                if (index >= contentEnd || source[index] != ';')
                {
                    break;
                }

                index++;
            }

            parameters = values.ToArray();
            return parameters.Length > 0;
        }

        private static bool TryReadNumber(string source, ref int index, int contentEnd, out long value)
        {
            value = 0;
            int start = index;
            while (index < contentEnd && char.IsAsciiDigit(source[index]))
            {
                int digit = source[index++] - '0';
                if (value > (long.MaxValue - digit) / 10)
                {
                    return false;
                }

                value = (value * 10) + digit;
            }

            return index > start;
        }

        private static bool TryAdd(long left, long right, out long result)
        {
            if (right > long.MaxValue - left)
            {
                result = 0;
                return false;
            }

            result = left + right;
            return true;
        }

        private static bool SetMaximum(ref long target, long value)
        {
            target = Math.Max(target, value);
            return true;
        }

        private static SixelParseStatus Invalid(out SixelMetadata metadata)
        {
            metadata = default;
            return SixelParseStatus.Invalid;
        }
    }
}
