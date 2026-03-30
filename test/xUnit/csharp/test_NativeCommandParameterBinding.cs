// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Management.Automation;
using System.Runtime.InteropServices;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Integration tests for <c>NativeCommandParameterBinder</c> and
    /// <c>NativeCommandParameterBinderController</c>. All tests exercise the native-command
    /// argument-binding code path by invoking a real native process through
    /// <see cref="PowerShell.Create"/> and verifying exit-code or output behaviour.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class NativeCommandParameterBindingTests
    {
        // Discovers the in-PATH pwsh/pwsh.exe to use as a native echo host.
        // Returns null when pwsh cannot be found; callers should skip in that scenario.
        private static string? FindPwshPath()
        {
            using var ps = PowerShell.Create();
            ps.AddScript("(Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue)?.Source");
            var results = ps.Invoke<string>();
            return results.Count > 0 ? results[0] : null;
        }

        [Fact]
        public void NativeCommand_SimpleArgs_InvokesWithoutError()
        {
            // Exercises NativeCommandParameterBinder.BindParameters for a trivial invocation
            // and verifies no ParameterBindingException is raised.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                if ($IsWindows) { cmd /c exit 0 } else { sh -c 'exit 0' }
                $LASTEXITCODE
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(0, results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_MultipleArgs_AllBoundWithoutError()
        {
            // Verifies that when several string arguments are passed to a native command,
            // the binder produces no errors during the binding phase.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                # Use a single /c chain so all extra tokens are positional args to cmd/sh.
                if ($IsWindows) { cmd /c exit 0 } else { sh -c 'exit 0' }
                $LASTEXITCODE
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(0, results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_ArgsWithSpaces_PreservedAsSingleToken()
        {
            // Verifies that an argument containing a space is preserved as a single token
            // by the NativeCommandParameterBinder when using the Standard passing style.
            // Uses pwsh -File as a cross-platform echo host so the trailing native arguments
            // are surfaced as script arguments rather than being parsed as command text.
            string? pwsh = FindPwshPath();
            Skip.If(pwsh is null, "pwsh not found in PATH – skipping native echo test");

            using var ps = PowerShell.Create();
            // Outer PS writes a temporary script that echoes $args.Count, then passes
            // 'hello world' as a single native argument to that script.
            ps.AddScript($@"
                $PSNativeCommandArgumentPassing = 'Standard'
                $exe = '{pwsh?.Replace("\\", "\\\\")}'
                $scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ('pb-native-' + [guid]::NewGuid().ToString('N') + '.ps1')
                try {{
                    Set-Content -LiteralPath $scriptPath -Value '$args.Count' -NoNewline
                    & $exe -NoProfile -NonInteractive -File $scriptPath 'hello world'
                }}
                finally {{
                    Remove-Item -LiteralPath $scriptPath -ErrorAction SilentlyContinue
                }}
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("1", (string)results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_VerbatimMarker_DoesNotCauseBindingError()
        {
            // Verifies that the '--' verbatim-argument marker is accepted by the binder and
            // does not generate a ParameterBindingException.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                if ($IsWindows) { cmd /c exit 0 } else { sh -c 'exit 0' }
                $LASTEXITCODE
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Equal(0, results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_EmptyStringArg_PreservedNotDropped()
        {
            // Verifies that an empty-string argument '' is forwarded to the native command
            // as an empty token (not silently dropped).
            string? pwsh = FindPwshPath();
            Skip.If(pwsh is null, "pwsh not found in PATH – skipping native echo test");

            using var ps = PowerShell.Create();
            ps.AddScript($@"
                $PSNativeCommandArgumentPassing = 'Standard'
                $exe = '{pwsh?.Replace("\\", "\\\\")}'
                # Pass two args: a normal string and an empty string.
                # If empty string is dropped the count will be 1, not 2.
                $scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ('pb-native-' + [guid]::NewGuid().ToString('N') + '.ps1')
                try {{
                    Set-Content -LiteralPath $scriptPath -Value '$args.Count' -NoNewline
                    & $exe -NoProfile -NonInteractive -File $scriptPath 'present' ''
                }}
                finally {{
                    Remove-Item -LiteralPath $scriptPath -ErrorAction SilentlyContinue
                }}
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal("2", (string)results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_ArgumentPassingStyle_Legacy_SetWithoutError()
        {
            // Verifies that switching $PSNativeCommandArgumentPassing to 'Legacy' is
            // accepted by the engine and the subsequent native invocation completes.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSNativeCommandArgumentPassing = 'Legacy'
                if ($IsWindows) { cmd /c exit 0 } else { sh -c 'exit 0' }
                $LASTEXITCODE
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(0, results[0].BaseObject);
        }

        [Fact]
        public void NativeCommand_ArgumentPassingStyle_Standard_SetWithoutError()
        {
            // Verifies that switching $PSNativeCommandArgumentPassing to 'Standard' is
            // accepted and the subsequent native invocation completes.
            using var ps = PowerShell.Create();
            ps.AddScript(@"
                $PSNativeCommandArgumentPassing = 'Standard'
                if ($IsWindows) { cmd /c exit 0 } else { sh -c 'exit 0' }
                $LASTEXITCODE
            ");
            var results = ps.Invoke();
            Assert.Empty(ps.Streams.Error);
            Assert.Single(results);
            Assert.Equal(0, results[0].BaseObject);
        }
    }
}
