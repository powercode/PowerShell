// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Reflection;
using Microsoft.PowerShell.Commands.Internal.Format;
using Xunit;

namespace PSTests.FormatAndOutput
{
    public class SixelImageLayoutTests
    {
        private static readonly TerminalImageSnapshot s_snapshot = new TerminalImageSnapshot(
            cellPixelWidth: 8,
            cellPixelHeight: 16,
            usableColumns: 80,
            viewportRows: 24,
            configurationGeneration: 1,
            viewportGeneration: 1);

        [Fact]
        public void ConvertsAcceptanceFixtureToTwentyByThreeCells()
        {
            FormattedText text = FormattedText.Parse("\x1bPq\"1;1;160;48!160~\x1b\\");

            SixelCellMetrics metrics = ImageLayout.MeasureImage(text.Tokens[0].SixelMetadata, s_snapshot);

            Assert.Equal(20, metrics.Width);
            Assert.Equal(3, metrics.Height);
        }

        [Fact]
        public void MeasuresImagesAtomicallyWithAdjacentText()
        {
            FormattedText text = FormattedText.Parse("left\x1bPq\"1;1;160;48!160~\x1b\\right");

            long width = ImageLayout.MeasureNaturalWidth(text, new DisplayCells(), s_snapshot);

            Assert.Equal(29, width);
        }

        [Fact]
        public void MeasuresLogicalLinesIndependently()
        {
            FormattedText text = FormattedText.Parse("12345\n12");

            long width = ImageLayout.MeasureNaturalWidth(text, new DisplayCells(), s_snapshot);

            Assert.Equal(5, width);
        }

        [Fact]
        public void MeasuresImageSourceForEligibleAndFallbackDestinations()
        {
            const string Image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            var displayCells = new DisplayCells();

            Assert.Equal(20, ImageLayout.MeasureNaturalWidthForOutput(Image, displayCells, s_snapshot));
            Assert.Equal(FormattedText.SixelFallback.Length, ImageLayout.MeasureNaturalWidthForOutput(Image, displayCells, default));
        }

        [Fact]
        public void CeilingDivisionDoesNotOverflowAtMaximumTraversal()
        {
            FormattedText text = FormattedText.Parse("\x1bPq!9223372036854775807@\x1b\\");

            SixelCellMetrics metrics = ImageLayout.MeasureImage(text.Tokens[0].SixelMetadata, s_snapshot);

            Assert.Equal(1152921504606846976, metrics.Width);
        }

        [Fact]
        public void RejectsMissingGeometry()
        {
            FormattedText text = FormattedText.Parse("\x1bPq~\x1b\\");

            Assert.Throws<InvalidOperationException>(() => ImageLayout.MeasureImage(text.Tokens[0].SixelMetadata, default));
        }

        [Fact]
        public void PlacesAdjacentTextAndFollowingLineOutsideImageRectangle()
        {
            const string image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            FormattedText text = FormattedText.Parse($"{image}right\nnext");

            FieldLayout layout = ImageLayout.LayoutField(text, 40, wrap: true, new DisplayCells(), s_snapshot);

            Assert.Collection(
                layout.Fragments,
                fragment =>
                {
                    Assert.True(fragment.IsImage);
                    Assert.Equal(image, fragment.Content);
                    Assert.Equal((0, 0, 20, 3), (fragment.Column, fragment.Row, fragment.Width, fragment.Height));
                },
                fragment =>
                {
                    Assert.False(fragment.IsImage);
                    Assert.Equal("right", fragment.Content);
                    Assert.Equal((20, 0), (fragment.Column, fragment.Row));
                },
                fragment =>
                {
                    Assert.Equal("next", fragment.Content);
                    Assert.Equal((0, 3), (fragment.Column, fragment.Row));
                });
            Assert.Equal(4, layout.OccupiedRows);
        }

        [Fact]
        public void TransparentImageDoesNotReserveEmptyTrailingBand()
        {
            var transparent = new SixelMetadata(
                p1: 0,
                p2: 1,
                p3: 0,
                traversalWidth: 1,
                paintedHeight: 1,
                bandHeight: 18,
                cursorTravelHeight: 18,
                declaredWidth: null,
                declaredHeight: null,
                pan: null,
                pad: null);
            var opaque = new SixelMetadata(
                p1: 0,
                p2: 0,
                p3: 0,
                traversalWidth: 1,
                paintedHeight: 1,
                bandHeight: 18,
                cursorTravelHeight: 18,
                declaredWidth: null,
                declaredHeight: null,
                pan: null,
                pad: null);

            Assert.Equal(1, ImageLayout.MeasureImage(transparent, s_snapshot).Height);
            Assert.Equal(2, ImageLayout.MeasureImage(opaque, s_snapshot).Height);
        }

        [Fact]
        public void ReplacesImageThatCannotFitField()
        {
            FormattedText text = FormattedText.Parse("\x1bPq\"1;1;160;48!160~\x1b\\");

            FieldLayout layout = ImageLayout.LayoutField(text, 10, wrap: true, new DisplayCells(), s_snapshot);

            Assert.Single(layout.Fragments);
            Assert.False(layout.Fragments[0].IsImage);
            Assert.Equal("[sixel]", layout.Fragments[0].Content);
            Assert.Equal(7, layout.Fragments[0].Width);
        }

        [Fact]
        public void WrapsImageBelowCurrentTallestLineItem()
        {
            const string image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            FormattedText text = FormattedText.Parse($"12345{image}{image}");

            FieldLayout layout = ImageLayout.LayoutField(text, 30, wrap: true, new DisplayCells(), s_snapshot);

            Assert.Equal((5, 0), (layout.Fragments[1].Column, layout.Fragments[1].Row));
            Assert.Equal((0, 3), (layout.Fragments[2].Column, layout.Fragments[2].Row));
            Assert.Equal(6, layout.OccupiedRows);
        }

        [Fact]
        public void ComposesFieldsAtColumnOffsetsUsingMaximumHeight()
        {
            const string image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            FieldLayout imageField = ImageLayout.LayoutField(FormattedText.Parse(image), 20, wrap: true, new DisplayCells(), s_snapshot);
            FieldLayout textField = ImageLayout.LayoutField(FormattedText.Parse("text"), 10, wrap: true, new DisplayCells(), s_snapshot);

            RowLayout row = ImageLayout.ComposeRow(new[] { imageField, textField }, new[] { 0, 21 });

            Assert.Equal(3, row.OccupiedRows);
            Assert.Equal((0, 0, true), (row.Fragments[0].Column, row.Fragments[0].Row, row.Fragments[0].IsImage));
            Assert.Equal((21, 0, false), (row.Fragments[1].Column, row.Fragments[1].Row, row.Fragments[1].IsImage));
        }

        [Fact]
        public void RightAlignsWholeLogicalLineWithinField()
        {
            AssertAlignedField(TextAlignment.Right);
        }

        [Fact]
        public void CentersWholeLogicalLineWithinField()
        {
            AssertAlignedField(TextAlignment.Center);
        }

        private static void AssertAlignedField(int alignment)
        {
            FormattedText text = FormattedText.Parse("\x1bPq\"1;1;16;6~\x1\\ab");
            FieldLayout field = ImageLayout.LayoutField(text, 10, wrap: false, new DisplayCells(), s_snapshot);
            int lineWidth = field.Fragments.Max(fragment => fragment.Column + fragment.Width);
            int remaining = 10 - lineWidth;
            int expectedOffset = alignment == TextAlignment.Right ? remaining : remaining / 2;

            FieldLayout aligned = ImageLayout.AlignField(field, 10, alignment);

            Assert.Equal(field.Fragments.Count, aligned.Fragments.Count);
            for (int index = 0; index < field.Fragments.Count; index++)
            {
                Assert.Equal(field.Fragments[index].Column + expectedOffset, aligned.Fragments[index].Column);
            }
        }

        [Fact]
        public void DefaultTerminalImageOutputIsUnavailableAndRejectsWrites()
        {
            TerminalImageOutput output = TerminalImageOutput.Unavailable;
            var row = new RowLayout(Array.Empty<FieldLayoutFragment>(), occupiedRows: 0);

            Assert.False(output.GetSnapshot().IsAvailable);
            Assert.False(output.TryWrite(row));
        }

        [Fact]
        public void LineOutputDefaultsToUnavailableImageOutput()
        {
            var output = new UnavailableLineOutput();
            var row = new RowLayout(Array.Empty<FieldLayoutFragment>(), occupiedRows: 0);

            Assert.False(output.GetTerminalImageSnapshot().IsAvailable);
            Assert.False(output.TryWriteRowLayout(row));
        }

        [Fact]
        public void TableWriterSubmitsImageRowToEligibleOutputExactlyOnce()
        {
            const string Image = "\x1bPq\"1;1;16;6~\x1b\\";
            var output = new FakeLineOutput(s_snapshot);
            var writer = new TableWriter();
            int[] widths = [10, 10];
            int[] alignments = [TextAlignment.Left, TextAlignment.Left];
            writer.Initialize(0, 21, widths, alignments, ReadOnlySpan<bool>.Empty, suppressHeader: true);

            writer.GenerateRow([Image + "tail", "next"], output, multiLine: false, alignments, output.DisplayCells, generatedRows: null);

            Assert.Empty(output.Lines);
            RowLayout row = Assert.Single(output.Layouts);
            Assert.Equal(s_snapshot.ConfigurationGeneration, row.Snapshot.ConfigurationGeneration);
            Assert.Equal(1, row.Fragments.Count(fragment => fragment.IsImage));
            Assert.Equal(Image, Assert.Single(row.Fragments, fragment => fragment.IsImage).Content);
        }

        [Theory]
        [InlineData(false, "value")]
        [InlineData(true, "value")]
        [InlineData(false, "\x1b[32mvalue\x1b[0m")]
        [InlineData(true, "\x1b[32mvalue\x1b[0m")]
        public void TableWriterSkipsImageServicesForOrdinaryText(bool available, string value)
        {
            var output = new FakeLineOutput(available ? s_snapshot : default);
            var writer = new TableWriter();
            int[] widths = [20];
            int[] alignments = [TextAlignment.Right];
            writer.Initialize(0, 20, widths, alignments, ReadOnlySpan<bool>.Empty, suppressHeader: true);

            writer.GenerateRow([value], output, multiLine: false, alignments, output.DisplayCells, generatedRows: null);

            Assert.Equal(0, output.SnapshotRequests);
            Assert.Empty(output.Layouts);
            Assert.Equal("value", new StringDecorated(Assert.Single(output.Lines)).ToString(OutputRendering.PlainText).Trim());
        }

        [Theory]
        [InlineData(false, "value")]
        [InlineData(true, "value")]
        [InlineData(false, "\x1b[32mvalue\x1b[0m")]
        [InlineData(true, "\x1b[32mvalue\x1b[0m")]
        public void ListWriterSkipsImageServicesForOrdinaryText(bool available, string value)
        {
            var output = new FakeLineOutput(available ? s_snapshot : default);
            var writer = new ListWriter();
            writer.Initialize(["Name"], 40, output.DisplayCells);

            writer.WriteProperties([value], output);

            Assert.Equal(0, output.SnapshotRequests);
            Assert.Empty(output.Layouts);
            Assert.Equal("Name : value", new StringDecorated(Assert.Single(output.Lines)).ToString(OutputRendering.PlainText));
        }

        [Fact]
        public void ListWriterSubmitsImagePropertyToEligibleOutputExactlyOnce()
        {
            const string Image = "\x1bPq\"1;1;16;6~\x1b\\";
            var output = new FakeLineOutput(s_snapshot);
            var writer = new ListWriter();
            writer.Initialize(["Image"], 40, output.DisplayCells);

            writer.WriteProperties([Image + "tail"], output);

            Assert.Empty(output.Lines);
            RowLayout row = Assert.Single(output.Layouts);
            FieldLayoutFragment image = Assert.Single(row.Fragments, fragment => fragment.IsImage);
            Assert.Equal(Image, image.Content);
            Assert.Equal("Image : ".Length, image.Column);
            string transaction = TerminalImageRenderer.Render(row);
            Assert.Contains("Image : ", transaction);
            Assert.True(transaction.IndexOf(Image, StringComparison.Ordinal) < transaction.IndexOf("Image : ", StringComparison.Ordinal));
        }

        [Fact]
        public void ComplexWriterSubmitsImageToEligibleOutputExactlyOnce()
        {
            const string Image = "\x1bPq\"1;1;16;6~\x1b\\";
            var output = new FakeLineOutput(s_snapshot);
            var writer = new ComplexWriter();
            writer.Initialize(output, 40);

            writer.WriteString(Image + "tail");

            Assert.Empty(output.Lines);
            RowLayout row = Assert.Single(output.Layouts);
            Assert.Equal(Image, Assert.Single(row.Fragments, fragment => fragment.IsImage).Content);
            Assert.Equal(1, CountOccurrences(TerminalImageRenderer.Render(row), Image));
        }

        [Fact]
        public void ComplexWriterUsesFallbackForUnavailableOutput()
        {
            const string Image = "\x1bPq\"1;1;16;6~\x1b\\";
            var output = new FakeLineOutput();
            var writer = new ComplexWriter();
            writer.Initialize(output, 40);

            writer.WriteString(Image + "tail");

            Assert.Equal([FormattedText.SixelFallback + "tail"], output.Lines);
            Assert.Empty(output.Layouts);
        }

        [Fact]
        public void ComplexWriterPreservesFallbackForFirstLineIndentation()
        {
            const string Image = "\x1bPq\"1;1;16;6~\x1b\\";
            var output = new FakeLineOutput(s_snapshot);
            var writer = new ComplexWriter();
            writer.Initialize(output, 40);
            var child = new FormatEntry
            {
                frameInfo = new FrameInfo
                {
                    leftIndentation = 1,
                    firstLine = 2,
                },
            };
            child.formatValueList.Add(new FormatPropertyField { propertyValue = Image + "tail" });
            child.formatValueList.Add(new FormatNewLine());
            var root = new FormatEntry();
            root.formatValueList.Add(child);

            writer.WriteObject([root]);

            Assert.Equal(["   " + FormattedText.SixelFallback + "tail", string.Empty], output.Lines);
            Assert.Empty(output.Layouts);
        }

        [Fact]
        public void WidePackingUsesImageNaturalWidth()
        {
            const string Image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            int width = ImageLayout.MeasureNaturalWidthForOutput(Image, new DisplayCells(), s_snapshot);

            Assert.Equal(3, TableWriter.ComputeWideViewBestItemsPerRowFit(width, screenColumns: 80));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CachedWidthsSkipImageServicesForOrdinaryText(bool wide, bool available)
        {
            var output = new FakeLineOutput(available ? s_snapshot : default);
            var command = new OutCommandInner { LineOutput = output };

            int width = MeasureCachedGroup(command, wide, [null, string.Empty, "value", "\x1b[32mvalue\x1b[0m"]);

            Assert.Equal(5, width);
            Assert.Equal(0, output.SnapshotRequests);
            Assert.Empty(output.Layouts);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CachedWidthsCaptureOneSnapshotPerCandidateGroup(bool wide, bool available)
        {
            const string Image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            var output = new FakeLineOutput(available ? s_snapshot : default);
            var command = new OutCommandInner { LineOutput = output };

            int width = MeasureCachedGroup(command, wide, ["value", Image, Image]);

            Assert.Equal(available ? 20 : FormattedText.SixelFallback.Length, width);
            Assert.Equal(1, output.SnapshotRequests);
            Assert.Equal(5, MeasureCachedGroup(command, wide, ["value"]));
            Assert.Equal(1, output.SnapshotRequests);
            Assert.Equal(width, MeasureCachedGroup(command, wide, [Image]));
            Assert.Equal(2, output.SnapshotRequests);
            Assert.Empty(output.Layouts);
        }

        private static int MeasureCachedGroup(OutCommandInner command, bool wide, string[] values)
        {
            var tableHeader = new TableHeaderInfo();
            tableHeader.tableColumnInfoList.Add(new TableColumnInfo { propertyName = "P" });
            var start = new FormatStartData { shapeInfo = wide ? new WideViewHeaderInfo() : tableHeader };
            var packets = new List<PacketInfoData>();
            foreach (string value in values)
            {
                var field = new FormatPropertyField { propertyValue = value };
                var tableRow = new TableRowEntry();
                tableRow.formatPropertyFieldList.Add(field);
                packets.Add(new FormatEntryData
                {
                    formatEntryInfo = wide ? new WideViewEntry { formatPropertyField = field } : tableRow,
                });
            }

            typeof(OutCommandInner).GetMethod("ProcessCachedGroup", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(command, [start, packets]);
            object hint = typeof(OutCommandInner).GetField("_formattingHint", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(command);
            object width = hint.GetType().GetField(wide ? "maxWidth" : "columnWidths", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(hint);
            return wide ? (int)width : ((int[])width)[0];
        }

        [Fact]
        public void RendererReservesDrawsImageOnceAndAdvancesBelowRow()
        {
            const string Image = "\x1bPq~\x1b\\";
            var row = new RowLayout(
                [
                    new FieldLayoutFragment("before", 0, 0, 6, 1, isImage: false),
                    new FieldLayoutFragment(Image, 6, 0, 1, 2, isImage: true),
                    new FieldLayoutFragment("after", 7, 0, 5, 1, isImage: false),
                ],
                occupiedRows: 2,
                s_snapshot);

            string transaction = TerminalImageRenderer.Render(row);

            Assert.Equal(1, CountOccurrences(transaction, Image));
            Assert.StartsWith("\r\n\n\x1b[2A", transaction);
            Assert.Contains("\x1b[7G\x1b" + "7" + Image + "\x1b" + "8", transaction);
            Assert.True(transaction.IndexOf(Image, StringComparison.Ordinal) < transaction.IndexOf("before", StringComparison.Ordinal));
            Assert.EndsWith("\x1b[2B\r", transaction);
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }

            return count;
        }

        private sealed class FakeLineOutput : LineOutput
        {
            private readonly TerminalImageSnapshot _snapshot;

            internal FakeLineOutput(TerminalImageSnapshot snapshot = default)
            {
                _snapshot = snapshot;
            }

            internal List<string> Lines { get; } = new();

            internal List<RowLayout> Layouts { get; } = new();

            internal int SnapshotRequests { get; private set; }

            internal override int ColumnNumber => 80;

            internal override int RowNumber => 40;

            internal override void WriteLine(string value)
            {
                Lines.Add(value);
            }

            internal override TerminalImageSnapshot GetTerminalImageSnapshot()
            {
                SnapshotRequests++;
                return _snapshot;
            }

            internal override bool TryWriteRowLayout(RowLayout layout)
            {
                Layouts.Add(layout);
                return true;
            }
        }

        private sealed class UnavailableLineOutput : LineOutput
        {
            internal override int ColumnNumber => 80;

            internal override int RowNumber => 40;

            internal override void WriteLine(string value)
            {
            }
        }
    }

    public class SixelConfigurationTests
    {
        [Fact]
        public void CapturesWindowsTerminalConfigurationAtomically()
        {
            var configuration = new PSStyle.SixelConfiguration();

            configuration.Configure(9, 18, SixelProfile.WindowsTerminal124);
            PSStyle.SixelConfiguration.Snapshot snapshot = configuration.GetSnapshot();

            Assert.Equal(SixelMode.Explicit, snapshot.Mode);
            Assert.Equal(9, snapshot.CellPixelWidth);
            Assert.Equal(18, snapshot.CellPixelHeight);
            Assert.Equal(SixelProfile.WindowsTerminal124, snapshot.Profile);
            Assert.Equal(1, snapshot.Generation);
        }
    }

    public class SixelFormattedTextTests
    {
        [Fact]
        public void IsolatesLineBreaksOutsideSixelPayloads()
        {
            const string image = "\x1bPq@\r\nA\x1b\\";
            FormattedText formattedText = FormattedText.Parse($"left{image}\r\nright");

            Assert.Collection(
                formattedText.Tokens,
                token => Assert.Equal(FormattedTextTokenKind.Text, token.Kind),
                token => Assert.Equal(FormattedTextTokenKind.InvalidSixel, token.Kind),
                token => Assert.Equal(FormattedTextTokenKind.LineBreak, token.Kind),
                token => Assert.Equal(FormattedTextTokenKind.Text, token.Kind));
        }

        [Fact]
        public void ReplacesEachImageCandidateBeforeFormatting()
        {
            const string valid = "\x1bPq~\x1b\\";
            const string invalid = "\x1bPq!~\x1b\\";
            FormattedText formattedText = FormattedText.Parse($"a{valid}b{invalid}c");

            Assert.True(formattedText.HasImages);
            Assert.True(formattedText.HasUnsupportedImageCandidates);
            Assert.Equal("a[sixel]b[sixel]c", formattedText.ToFallbackString());
        }

        [Fact]
        public void PreservesOpaqueDcsInFallbackProjection()
        {
            const string opaque = "\x1bP$qpayload\x1b\\";
            FormattedText formattedText = FormattedText.Parse($"before{opaque}after");

            Assert.False(formattedText.HasImages);
            Assert.False(formattedText.HasUnsupportedImageCandidates);
            Assert.Equal(opaque, formattedText.Source.Substring(formattedText.Tokens[1].Start, formattedText.Tokens[1].Length));
            Assert.Equal($"before{opaque}after", formattedText.ToFallbackString());
        }

        [Fact]
        public void TreatsC1DcsAsOneUnsupportedCandidate()
        {
            const string candidate = "\x90q~\x9c";
            FormattedText formattedText = FormattedText.Parse($"a{candidate}b");

            Assert.Equal(FormattedTextTokenKind.InvalidSixel, formattedText.Tokens[1].Kind);
            Assert.Equal("a[sixel]b", formattedText.ToFallbackString());
        }

        [Fact]
        public void FastCheckRejectsOrdinaryText()
        {
            Assert.False(FormattedText.MightContainImage("ordinary text"));
            Assert.True(FormattedText.MightContainImage("\x1bPq~\x1b\\"));
        }

        [Fact]
        public void FallbackHelperReturnsOrdinaryStringInstance()
        {
            const string text = "ordinary text";

            Assert.Same(text, FormattedText.GetFallbackIfNeeded(text));
        }

        [Fact]
        public void PlainTextProjectionRemovesImageWithoutSplittingPayloadLineBreaks()
        {
            const string image = "\x1bPq@\r\nA\x1b\\";
            FormattedText formattedText = FormattedText.Parse($"before{image}after");

            Assert.Equal("beforeafter", formattedText.ToPlainTextString());
        }
    }

    public class SixelParserTests
    {
        [Fact]
        public void ParsesDeclaredSquarePixelGeometryWithoutRewritingSource()
        {
            const string image = "\x1bPq\"1;1;160;48!160~\x1b\\";
            string source = $"before{image}after";

            Assert.True(SixelParser.TryParseCandidate(source, 6, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Valid, result.Status);
            Assert.Equal(image, source.Substring(result.Start, result.Length));
            Assert.Equal(160, result.Metadata.DeclaredWidth);
            Assert.Equal(48, result.Metadata.DeclaredHeight);
            Assert.Equal(160, result.Metadata.TraversalWidth);
            Assert.Equal(6, result.Metadata.PaintedHeight);
        }

        [Fact]
        public void RepeatExtentIsConstantInEncodedSize()
        {
            const string image = "\x1bPq!9223372036854775807@\x1b\\";

            Assert.True(SixelParser.TryParseCandidate(image, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Valid, result.Status);
            Assert.Equal(long.MaxValue, result.Metadata.TraversalWidth);
            Assert.Equal(1, result.Metadata.PaintedHeight);
        }

        [Fact]
        public void PreservesOptionalHeaderParameters()
        {
            const string image = "\x1bP1;;3q~\x1b\\";

            Assert.True(SixelParser.TryParseCandidate(image, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Valid, result.Status);
            Assert.Equal(1, result.Metadata.P1);
            Assert.Null(result.Metadata.P2);
            Assert.Equal(3, result.Metadata.P3);
        }

        [Fact]
        public void RejectsOverflowingHeaderParameter()
        {
            const string image = "\x1bP9223372036854775808q~\x1b\\";

            Assert.True(SixelParser.TryParseCandidate(image, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Invalid, result.Status);
            Assert.Equal(image.Length, result.Length);
        }

        [Theory]
        [InlineData("\x1bP$qpayload\x1b\\")]
        [InlineData("\x1bP+qpayload\x1b\\")]
        [InlineData("\x1bP1;2;3;4q~\x1b\\")]
        public void DoesNotTreatUnsupportedHeadersAsValidImages(string candidate)
        {
            Assert.True(SixelParser.TryParseCandidate(candidate, 0, out SixelParseResult result));

            Assert.NotEqual(SixelParseStatus.Valid, result.Status);
            Assert.Equal(candidate.Length, result.Length);
        }

        [Fact]
        public void UnterminatedCandidateConsumesRemainingSourceForAtomicFallback()
        {
            const string candidate = "\x1bPq~unterminated";

            Assert.True(SixelParser.TryParseCandidate(candidate, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Invalid, result.Status);
            Assert.Equal(candidate.Length, result.Length);
        }

        [Fact]
        public void UnexpectedEscapeEndsInvalidCandidateBeforeFollowingControl()
        {
            const string source = "\x1bPq~\x1b[31mtext";

            Assert.True(SixelParser.TryParseCandidate(source, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Invalid, result.Status);
            Assert.Equal(4, result.Length);
            Assert.Equal('\x1b', source[result.Start + result.Length]);
        }

        [Fact]
        public void TracksPaintedBandsSeparatelyFromCursorTravel()
        {
            const string image = "\x1bPq@-\x1b\\";

            Assert.True(SixelParser.TryParseCandidate(image, 0, out SixelParseResult result));

            Assert.Equal(SixelParseStatus.Valid, result.Status);
            Assert.Equal(1, result.Metadata.PaintedHeight);
            Assert.Equal(6, result.Metadata.BandHeight);
            Assert.Equal(12, result.Metadata.CursorTravelHeight);
        }
    }
}
