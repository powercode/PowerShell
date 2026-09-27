// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Threading;

using Microsoft.PowerShell.Commands.Internal.Format;

namespace Microsoft.PowerShell
{
    internal partial class ConsoleHostUserInterface
    {
        /// <summary>
        /// Owns destination-local Sixel eligibility and serializes prepared image rows through the console output path.
        /// </summary>
        private sealed class TerminalImageSession : TerminalImageOutput
        {
            private readonly ConsoleHostUserInterface _owner;
            private long _failedConfigurationGeneration = -1;

            internal TerminalImageSession(ConsoleHostUserInterface owner)
            {
                _owner = owner;
            }

            /// <summary>
            /// Captures explicit configuration and existing viewport state without issuing terminal queries.
            /// </summary>
            internal override TerminalImageSnapshot GetSnapshot()
            {
                PSStyle.SixelConfiguration.Snapshot configuration = PSStyle.Instance.Sixel.GetSnapshot();
                if (configuration.Mode != SixelMode.Explicit
                    || configuration.Profile != SixelProfile.WindowsTerminal124
                    || configuration.Generation == Volatile.Read(ref _failedConfigurationGeneration)
                    || !_owner.SupportsVirtualTerminal
                    || Console.IsOutputRedirected
                    || _owner.TranscribeOnly
                    || _owner._parent.IsTranscribing
                    || _owner._parent.IsRunningAsync
                    || PSStyle.Instance.OutputRendering == OutputRendering.PlainText)
                {
                    return default;
                }

                try
                {
                    Size viewport = _owner.RawUI.WindowSize;
                    if (viewport.Width <= 1 || viewport.Height <= 1)
                    {
                        return default;
                    }

                    long viewportGeneration = ((long)viewport.Width << 32) | (uint)viewport.Height;
                    return new TerminalImageSnapshot(
                        configuration.CellPixelWidth,
                        configuration.CellPixelHeight,
                        viewport.Width - 1,
                        viewport.Height,
                        configuration.Generation,
                        viewportGeneration);
                }
                catch (Exception)
                {
                    return default;
                }
            }

            /// <summary>
            /// Revalidates the prepared snapshot and emits the complete transaction under the console output lock.
            /// </summary>
            internal override bool TryWrite(RowLayout layout)
            {
                ArgumentNullException.ThrowIfNull(layout);

                lock (_owner._instanceLock)
                {
                    TerminalImageSnapshot current = GetSnapshot();
                    if (!Matches(layout.Snapshot, current))
                    {
                        return false;
                    }

                    try
                    {
                        string transaction = TerminalImageRenderer.Render(layout);
                        _owner.WriteToConsole(transaction.AsSpan(), transcribeResult: false);
                        return true;
                    }
                    catch
                    {
                        Volatile.Write(ref _failedConfigurationGeneration, layout.Snapshot.ConfigurationGeneration);
                        throw;
                    }
                }
            }

            private static bool Matches(TerminalImageSnapshot expected, TerminalImageSnapshot current)
            {
                return expected.IsAvailable
                    && current.IsAvailable
                    && expected.CellPixelWidth == current.CellPixelWidth
                    && expected.CellPixelHeight == current.CellPixelHeight
                    && expected.UsableColumns == current.UsableColumns
                    && expected.ViewportRows == current.ViewportRows
                    && expected.ConfigurationGeneration == current.ConfigurationGeneration
                    && expected.ViewportGeneration == current.ViewportGeneration;
            }
        }
    }
}
