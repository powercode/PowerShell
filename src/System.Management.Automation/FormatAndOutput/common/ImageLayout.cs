// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Management.Automation.Internal;
using System.Text;

namespace Microsoft.PowerShell.Commands.Internal.Format
{
    /// <summary>
    /// Captures destination-local eligibility and geometry for one layout pass.
    /// Measurement must use one immutable snapshot because cell size, viewport size, or output eligibility may change
    /// before the prepared row is written. The row carries this snapshot to the host, which compares both generations
    /// with current state and rejects stale layouts instead of rendering with different geometry.
    /// </summary>
    internal readonly struct TerminalImageSnapshot
    {
        internal TerminalImageSnapshot(
            int cellPixelWidth,
            int cellPixelHeight,
            int usableColumns,
            int viewportRows,
            long configurationGeneration,
            long viewportGeneration)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixelWidth);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixelHeight);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(usableColumns);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportRows);

            IsAvailable = true;
            CellPixelWidth = cellPixelWidth;
            CellPixelHeight = cellPixelHeight;
            UsableColumns = usableColumns;
            ViewportRows = viewportRows;
            ConfigurationGeneration = configurationGeneration;
            ViewportGeneration = viewportGeneration;
        }

        internal bool IsAvailable { get; }

        internal int CellPixelWidth { get; }

        internal int CellPixelHeight { get; }

        internal int UsableColumns { get; }

        internal int ViewportRows { get; }

        internal long ConfigurationGeneration { get; }

        internal long ViewportGeneration { get; }
    }

    /// <summary>
    /// Carries the cell rectangle and cursor-travel guard derived from one image token.
    /// </summary>
    internal readonly struct SixelCellMetrics
    {
        internal SixelCellMetrics(long width, long height, long cursorTravelHeight)
        {
            Width = width;
            Height = height;
            CursorTravelHeight = cursorTravelHeight;
        }

        internal long Width { get; }

        internal long Height { get; }

        internal long CursorTravelHeight { get; }
    }

    /// <summary>
    /// Describes one positioned text or original-source image span in a rectangular field.
    /// </summary>
    internal readonly struct FieldLayoutFragment
    {
        internal FieldLayoutFragment(string content, int column, int row, int width, int height, bool isImage)
        {
            Content = content;
            Column = column;
            Row = row;
            Width = width;
            Height = height;
            IsImage = isImage;
        }

        internal string Content { get; }

        internal int Column { get; }

        internal int Row { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal bool IsImage { get; }
    }

    /// <summary>
    /// Carries positioned field content and its occupied row count for row composition.
    /// </summary>
    internal sealed class FieldLayout
    {
        internal FieldLayout(IReadOnlyList<FieldLayoutFragment> fragments, int occupiedRows)
        {
            Fragments = fragments;
            OccupiedRows = occupiedRows;
        }

        internal IReadOnlyList<FieldLayoutFragment> Fragments { get; }

        internal int OccupiedRows { get; }
    }

    /// <summary>
    /// Carries the translated fragments and physical height of one composed formatter row.
    /// </summary>
    internal sealed class RowLayout
    {
        internal RowLayout(
            IReadOnlyList<FieldLayoutFragment> fragments,
            int occupiedRows,
            TerminalImageSnapshot snapshot = default)
        {
            Fragments = fragments;
            OccupiedRows = occupiedRows;
            Snapshot = snapshot;
        }

        internal IReadOnlyList<FieldLayoutFragment> Fragments { get; }

        internal int OccupiedRows { get; }

        internal TerminalImageSnapshot Snapshot { get; }
    }

    /// <summary>
    /// Defines the output-only host boundary for snapshots and atomic prepared-row rendering.
    /// </summary>
    internal abstract class TerminalImageOutput
    {
        internal static TerminalImageOutput Unavailable { get; } = new UnavailableTerminalImageOutput();

        /// <summary>
        /// Captures the current destination geometry and eligibility for one layout pass.
        /// </summary>
        internal abstract TerminalImageSnapshot GetSnapshot();

        /// <summary>
        /// Attempts to emit a prepared row atomically, returning false if its destination snapshot is no longer current.
        /// </summary>
        internal abstract bool TryWrite(RowLayout layout);

        /// <summary>
        /// Supplies the safe default for hosts that do not implement managed terminal images.
        /// </summary>
        private sealed class UnavailableTerminalImageOutput : TerminalImageOutput
        {
            internal override TerminalImageSnapshot GetSnapshot() => default;

            internal override bool TryWrite(RowLayout layout)
            {
                ArgumentNullException.ThrowIfNull(layout);
                return false;
            }
        }
    }

    /// <summary>
    /// Builds a single terminal transaction from an already validated row layout.
    /// </summary>
    internal static class TerminalImageRenderer
    {
        private const string SaveCursor = "\x1b" + "7";
        private const string RestoreCursor = "\x1b" + "8";

        /// <summary>
        /// Produces one reserve, draw, and advance sequence without performing terminal I/O.
        /// </summary>
        internal static string Render(RowLayout layout)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (!layout.Snapshot.IsAvailable || layout.OccupiedRows <= 0 || layout.OccupiedRows >= layout.Snapshot.ViewportRows)
            {
                throw new InvalidOperationException("The prepared terminal image row is not renderable.");
            }

            var orderedFragments = new List<FieldLayoutFragment>(layout.Fragments);
            orderedFragments.Sort(static (left, right) =>
            {
                int rowComparison = left.Row.CompareTo(right.Row);
                return rowComparison != 0 ? rowComparison : left.Column.CompareTo(right.Column);
            });

            var output = new StringBuilder();
            output.Append('\r');
            output.Append('\n', layout.OccupiedRows);
            AppendCsiMovement(output, layout.OccupiedRows, 'A');

            int currentRow = 0;
            WriteFragments(output, orderedFragments, images: true, layout.Snapshot.UsableColumns, ref currentRow);
            AppendCsiMovement(output, currentRow, 'A');
            currentRow = 0;
            WriteFragments(output, orderedFragments, images: false, layout.Snapshot.UsableColumns, ref currentRow);

            AppendCsiMovement(output, layout.OccupiedRows - currentRow, 'B');
            output.Append('\r');
            return output.ToString();
        }

        private static void WriteFragments(
            StringBuilder output,
            List<FieldLayoutFragment> fragments,
            bool images,
            int usableColumns,
            ref int currentRow)
        {
            foreach (FieldLayoutFragment fragment in fragments)
            {
                if (fragment.IsImage != images)
                {
                    continue;
                }

                if (fragment.Row < currentRow || fragment.Column < 0 || fragment.Column + fragment.Width > usableColumns)
                {
                    throw new InvalidOperationException("A fragment falls outside the prepared terminal image row.");
                }

                AppendCsiMovement(output, fragment.Row - currentRow, 'B');
                AppendCsiMovement(output, fragment.Column + 1, 'G');
                currentRow = fragment.Row;
                if (images)
                {
                    output.Append(SaveCursor);
                    output.Append(fragment.Content);
                    output.Append(RestoreCursor);
                }
                else
                {
                    output.Append(fragment.Content);
                }
            }
        }

        private static void AppendCsiMovement(StringBuilder output, int amount, char command)
        {
            if (amount > 0)
            {
                output.Append("\x1b[");
                output.Append(amount);
                output.Append(command);
            }
        }
    }

    /// <summary>
    /// Converts parsed image metadata and text spans into stable cell measurements for formatter policy.
    /// </summary>
    internal static class ImageLayout
    {
        /// <summary>
        /// Converts the image's pixel extents and cursor travel into destination cell measurements.
        /// </summary>
        internal static SixelCellMetrics MeasureImage(SixelMetadata metadata, TerminalImageSnapshot snapshot)
        {
            if (!snapshot.IsAvailable)
            {
                throw new InvalidOperationException("Terminal image geometry is unavailable.");
            }

            long pixelWidth = Math.Max(metadata.TraversalWidth, metadata.DeclaredWidth ?? 0);
            long contentHeight = metadata.P2 == 1 ? metadata.PaintedHeight : metadata.BandHeight;
            long pixelHeight = Math.Max(contentHeight, metadata.DeclaredHeight ?? 0);
            return new SixelCellMetrics(
                CeilingDivide(pixelWidth, snapshot.CellPixelWidth),
                CeilingDivide(pixelHeight, snapshot.CellPixelHeight),
                CeilingDivide(metadata.CursorTravelHeight, snapshot.CellPixelHeight));
        }

        /// <summary>
        /// Measures the widest logical line while treating each image as one indivisible rectangle.
        /// </summary>
        internal static long MeasureNaturalWidth(FormattedText text, DisplayCells displayCells, TerminalImageSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(displayCells);

            long currentLineWidth = 0;
            long maximumWidth = 0;
            foreach (FormattedTextToken token in text.Tokens)
            {
                if (token.Kind == FormattedTextTokenKind.LineBreak)
                {
                    maximumWidth = Math.Max(maximumWidth, currentLineWidth);
                    currentLineWidth = 0;
                    continue;
                }

                long tokenWidth = MeasureToken(text, token, displayCells, snapshot);
                if (!TryAdd(currentLineWidth, tokenWidth, out currentLineWidth))
                {
                    throw new OverflowException("Formatted content width exceeds the supported range.");
                }
            }

            return Math.Max(maximumWidth, currentLineWidth);
        }

        /// <summary>
        /// Measures source text using image geometry when available and formatter fallback otherwise.
        /// </summary>
        internal static int MeasureNaturalWidthForOutput(
            string source,
            DisplayCells displayCells,
            TerminalImageSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(displayCells);

            if (!FormattedText.MightContainImage(source))
            {
                return displayCells.Length(source);
            }

            FormattedText formatted = FormattedText.Parse(source);
            if (!snapshot.IsAvailable || !formatted.HasImages)
            {
                return displayCells.Length(formatted.ToFallbackString());
            }

            long width = MeasureNaturalWidth(formatted, displayCells, snapshot);
            return width > int.MaxValue ? int.MaxValue : (int)width;
        }

        /// <summary>
        /// Fits formatted tokens into one field without splitting image payloads or flowing text through image rectangles.
        /// </summary>
        internal static FieldLayout LayoutField(
            FormattedText text,
            int fieldWidth,
            bool wrap,
            DisplayCells displayCells,
            TerminalImageSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(displayCells);

            if (fieldWidth <= 0)
            {
                return new FieldLayout(Array.Empty<FieldLayoutFragment>(), occupiedRows: 0);
            }

            var fragments = new List<FieldLayoutFragment>();
            int column = 0;
            int row = 0;
            int lineHeight = 1;

            foreach (FormattedTextToken token in text.Tokens)
            {
                if (token.Kind == FormattedTextTokenKind.LineBreak)
                {
                    AdvanceLine(ref column, ref row, ref lineHeight);
                    continue;
                }

                if (token.Kind == FormattedTextTokenKind.Sixel)
                {
                    SixelCellMetrics metrics = MeasureImage(token.SixelMetadata, snapshot);
                    if (CanPlaceImage(metrics, fieldWidth, snapshot))
                    {
                        PlaceImage(text, token, metrics, fieldWidth, wrap, displayCells, fragments, ref column, ref row, ref lineHeight);
                        continue;
                    }

                    PlaceText(FormattedText.SixelFallback, fieldWidth, wrap, displayCells, fragments, ref column, ref row, ref lineHeight);
                    continue;
                }

                string content = token.Kind == FormattedTextTokenKind.InvalidSixel
                    ? FormattedText.SixelFallback
                    : text.Source.Substring(token.Start, token.Length);
                PlaceText(content, fieldWidth, wrap, displayCells, fragments, ref column, ref row, ref lineHeight);
            }

            int occupiedRows = fragments.Count == 0 ? 0 : row + lineHeight;
            return new FieldLayout(fragments, occupiedRows);
        }

        /// <summary>
        /// Translates independently laid-out fields into one top-aligned formatter row.
        /// </summary>
        internal static RowLayout ComposeRow(
            IReadOnlyList<FieldLayout> fields,
            IReadOnlyList<int> startColumns,
            TerminalImageSnapshot snapshot = default)
        {
            ArgumentNullException.ThrowIfNull(fields);
            ArgumentNullException.ThrowIfNull(startColumns);
            if (fields.Count != startColumns.Count)
            {
                throw new ArgumentException("Each field requires one start column.", nameof(startColumns));
            }

            var fragments = new List<FieldLayoutFragment>();
            int occupiedRows = 0;
            for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
            {
                FieldLayout field = fields[fieldIndex] ?? throw new ArgumentException("Fields cannot contain null values.", nameof(fields));
                int startColumn = startColumns[fieldIndex];
                ArgumentOutOfRangeException.ThrowIfNegative(startColumn);
                occupiedRows = Math.Max(occupiedRows, field.OccupiedRows);

                foreach (FieldLayoutFragment fragment in field.Fragments)
                {
                    fragments.Add(new FieldLayoutFragment(
                        fragment.Content,
                        checked(startColumn + fragment.Column),
                        fragment.Row,
                        fragment.Width,
                        fragment.Height,
                        fragment.IsImage));
                }
            }

            return new RowLayout(fragments, occupiedRows, snapshot);
        }

        /// <summary>
        /// Applies text alignment to each logical line by translating all of its text and image fragments together.
        /// </summary>
        internal static FieldLayout AlignField(FieldLayout field, int fieldWidth, int alignment)
        {
            ArgumentNullException.ThrowIfNull(field);
            ArgumentOutOfRangeException.ThrowIfNegative(fieldWidth);
            if (alignment == TextAlignment.Left || alignment == TextAlignment.Undefined || field.Fragments.Count == 0)
            {
                return field;
            }

            var lineWidths = new Dictionary<int, int>();
            foreach (FieldLayoutFragment fragment in field.Fragments)
            {
                int right = checked(fragment.Column + fragment.Width);
                if (!lineWidths.TryGetValue(fragment.Row, out int currentRight) || right > currentRight)
                {
                    lineWidths[fragment.Row] = right;
                }
            }

            var fragments = new List<FieldLayoutFragment>(field.Fragments.Count);
            foreach (FieldLayoutFragment fragment in field.Fragments)
            {
                int remaining = Math.Max(0, fieldWidth - lineWidths[fragment.Row]);
                int offset = alignment == TextAlignment.Right ? remaining : remaining / 2;
                fragments.Add(new FieldLayoutFragment(
                    fragment.Content,
                    checked(fragment.Column + offset),
                    fragment.Row,
                    fragment.Width,
                    fragment.Height,
                    fragment.IsImage));
            }

            return new FieldLayout(fragments, field.OccupiedRows);
        }

        private static bool CanPlaceImage(SixelCellMetrics metrics, int fieldWidth, TerminalImageSnapshot snapshot)
        {
            return metrics.Width > 0
                && metrics.Width <= fieldWidth
                && metrics.Height > 0
                && metrics.Height < snapshot.ViewportRows
                && metrics.CursorTravelHeight < snapshot.ViewportRows;
        }

        private static void PlaceImage(
            FormattedText text,
            FormattedTextToken token,
            SixelCellMetrics metrics,
            int fieldWidth,
            bool wrap,
            DisplayCells displayCells,
            List<FieldLayoutFragment> fragments,
            ref int column,
            ref int row,
            ref int lineHeight)
        {
            int width = checked((int)metrics.Width);
            if (column + width > fieldWidth)
            {
                if (!wrap)
                {
                    PlaceText(FormattedText.SixelFallback, fieldWidth, wrap: false, displayCells, fragments, ref column, ref row, ref lineHeight);
                    return;
                }

                AdvanceLine(ref column, ref row, ref lineHeight);
            }

            int height = checked((int)metrics.Height);
            fragments.Add(new FieldLayoutFragment(
                text.Source.Substring(token.Start, token.Length),
                column,
                row,
                width,
                height,
                isImage: true));
            column += width;
            lineHeight = Math.Max(lineHeight, height);
        }

        private static void PlaceText(
            string content,
            int fieldWidth,
            bool wrap,
            DisplayCells displayCells,
            List<FieldLayoutFragment> fragments,
            ref int column,
            ref int row,
            ref int lineHeight)
        {
            int sourceIndex = 0;
            while (sourceIndex < content.Length)
            {
                int runStart = sourceIndex;
                int runColumn = column;
                while (sourceIndex < content.Length)
                {
                    int characterWidth = displayCells.Length(content[sourceIndex]);
                    if (column + characterWidth > fieldWidth)
                    {
                        break;
                    }

                    column += characterWidth;
                    sourceIndex++;
                }

                if (sourceIndex > runStart)
                {
                    fragments.Add(new FieldLayoutFragment(
                        content.Substring(runStart, sourceIndex - runStart),
                        runColumn,
                        row,
                        column - runColumn,
                        height: 1,
                        isImage: false));
                }

                if (sourceIndex == content.Length || !wrap)
                {
                    return;
                }

                AdvanceLine(ref column, ref row, ref lineHeight);
            }
        }

        private static void AdvanceLine(ref int column, ref int row, ref int lineHeight)
        {
            row += lineHeight;
            column = 0;
            lineHeight = 1;
        }

        private static long MeasureToken(
            FormattedText text,
            FormattedTextToken token,
            DisplayCells displayCells,
            TerminalImageSnapshot snapshot)
        {
            if (token.Kind == FormattedTextTokenKind.Sixel)
            {
                return MeasureImage(token.SixelMetadata, snapshot).Width;
            }

            if (token.Kind == FormattedTextTokenKind.InvalidSixel)
            {
                return FormattedText.SixelFallback.Length;
            }

            long width = 0;
            int end = token.Start + token.Length;
            for (int index = token.Start; index < end; index++)
            {
                if (!TryAdd(width, displayCells.Length(text.Source[index]), out width))
                {
                    throw new OverflowException("Formatted content width exceeds the supported range.");
                }
            }

            return width;
        }

        private static long CeilingDivide(long value, int divisor)
        {
            long quotient = value / divisor;
            return value % divisor == 0 ? quotient : quotient + 1;
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
    }
}
