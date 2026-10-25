// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using Microsoft.PowerShell;
using Xunit;

namespace PSTests.Parallel
{
    /// <summary>
    /// Tests for <c>MinishellParameterBinderController</c> and the subset of
    /// <c>CommandLineParameterParser</c> that handles minishell invocations
    /// (<c>-Command</c>, <c>-OutputFormat</c>, <c>-InputFormat</c>, <c>-NonInteractive</c>).
    /// Process-spawning tests use <see cref="SkippableFactAttribute"/> so they are skipped
    /// automatically when a <c>pwsh</c> executable is not discoverable in PATH.
    /// </summary>
    [Trait("Category", "ParameterBinding")]
    public class MinishellParameterBindingTests
    {
        // Finds the first pwsh / pwsh.exe in the system PATH.
        // Returns null when not found so callers can skip with Skip.If.
        private static string? FindPwshInPath()
        {
            string exe = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? "pwsh.exe" : "pwsh";

            string? pathVar = System.Environment.GetEnvironmentVariable("PATH");
            if (pathVar is null) return null;

            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                string full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        // Runs pwsh with the given arguments and returns (stdout, exitCode).
        private static (string stdout, int exitCode) RunPwsh(string pwshPath, string args)
        {
            var info = new ProcessStartInfo
            {
                FileName = pwshPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(info)!;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return (output, proc.ExitCode);
        }

        // ── Parsing-level tests (no process spawn required) ─────────────────────────

        [Fact]
        public void Minishell_CommandParameter_ParsedAndStored()
        {
            // -Command sets InitialCommand; no error should be generated.
            var cpp = new CommandLineParameterParser();
            cpp.Parse(new[] { "-Command", "Write-Output 'hello'" });
            Assert.False(cpp.AbortStartup);
            Assert.Equal("Write-Output 'hello'", cpp.InitialCommand);
            Assert.Null(cpp.ErrorMessage);
        }

        [Fact]
        public void Minishell_NonInteractiveFlag_Parsed()
        {
            // -NonInteractive must flip NonInteractive and not set an error.
            var cpp = new CommandLineParameterParser();
            cpp.Parse(new[] { "-NonInteractive" });
            Assert.False(cpp.AbortStartup);
            Assert.True(cpp.NonInteractive);
            Assert.Null(cpp.ErrorMessage);
        }

        [Fact]
        public void Minishell_OutputFormat_XML_ParsedCorrectly()
        {
            // -OutputFormat XML must set the output format to Xml and flag it as specified.
            var cpp = new CommandLineParameterParser();
            cpp.Parse(new[] { "-OutputFormat", "XML" });
            Assert.False(cpp.AbortStartup);
            Assert.Equal(Microsoft.PowerShell.Serialization.DataFormat.XML, cpp.OutputFormat);
            Assert.True(cpp.OutputFormatSpecified);
            Assert.Null(cpp.ErrorMessage);
        }

        [Fact]
        public void Minishell_InputFormat_XML_ParsedCorrectly()
        {
            // -InputFormat XML must set the input-format field without error.
            var cpp = new CommandLineParameterParser();
            cpp.Parse(new[] { "-InputFormat", "XML" });
            Assert.False(cpp.AbortStartup);
            Assert.Equal(Microsoft.PowerShell.Serialization.DataFormat.XML, cpp.InputFormat);
            Assert.Null(cpp.ErrorMessage);
        }

        // ── Process-level tests (skip when pwsh not in PATH) ────────────────────────

        [SkippableFact]
        public void Minishell_CommandParameter_ExecutesAndReturnsOutput()
        {
            // pwsh -Command "1+1" must print "2" to stdout and exit with 0.
            string? pwsh = FindPwshInPath();
            Skip.If(pwsh is null, "pwsh not found in PATH");

            var (stdout, exitCode) = RunPwsh(pwsh!, "-NoProfile -NonInteractive -Command \"Write-Output (1+1)\"");
            Assert.Equal(0, exitCode);
            Assert.Equal("2", stdout);
        }

        [SkippableFact]
        public void Minishell_NonInteractive_MissingMandatory_ExitsNonZero()
        {
            // In -NonInteractive mode a missing mandatory parameter must not prompt; the
            // host should fail with a non-zero exit code instead.
            string? pwsh = FindPwshInPath();
            Skip.If(pwsh is null, "pwsh not found in PATH");

            var (_, exitCode) = RunPwsh(
                pwsh!,
                "-NoProfile -NonInteractive -Command \"& { param([Parameter(Mandatory)][string]$X) $X }\"");
            Assert.NotEqual(0, exitCode);
        }
    }
}
