// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PSTests.Sequential;

public class ScopedRuntimeResolutionTests
{
    private static PSObject InvokeSingle(PowerShell shell, string script)
    {
        shell.Commands.Clear();
        var results = shell.AddScript(script).Invoke<PSObject>();
        Assert.Empty(shell.Streams.Error);
        return Assert.Single(results);
    }

    private static bool Boolean(PSObject value, string name)
        => Assert.IsType<bool>(value.Properties[name].Value);

    [Fact]
    public void RuntimeMembersExposeLegacyValuesAndReferences()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;

        var result = InvokeSingle(shell, """
            $null = $global:LASTEXITCODE = 17
            $null = $global:StackTrace = 'scoped-runtime-stack'
            function Get-ScopedRuntimeIdentity {
                [CmdletBinding()]
                param([string] $Name)
                'match' -match '(?<value>match)' | Out-Null
                $null = $errorValues = $PSSettings.Diagnostics.Errors
                $null = $errorMarker = 'scoped-runtime-error-marker'
                $errorValues.Add($errorMarker) | Out-Null
                $null = $errorShared = [object]::ReferenceEquals($errorValues, $Error) -and $Error.Contains($errorMarker)
                $errorValues.Remove($errorMarker) | Out-Null

                $null = $versionValues = $PSSettings.Runtime.VersionTable
                $null = $versionMarker = 'ScopedRuntimeMarker'
                $null = $versionValues[$versionMarker] = 'visible'
                $null = $versionShared = [object]::ReferenceEquals($versionValues, $PSVersionTable) -and $PSVersionTable.ContainsKey($versionMarker)
                $versionValues.Remove($versionMarker) | Out-Null

                $null = $foreachShared = $false
                foreach ($item in 1) {
                    $null = $foreachShared = [object]::ReferenceEquals($foreach, $PSSettings.Invocation.ForEachEnumerator)
                }
                $null = $switchShared = $false
                switch -Regex ('switch-value') {
                    'switch' { $null = $switchShared = [object]::ReferenceEquals($switch, $PSSettings.Invocation.SwitchEnumerator) }
                }

                [pscustomobject]@{
                    ArgumentsShared = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
                    InputShared = [object]::ReferenceEquals($input, $PSSettings.Invocation.Input)
                    ThisShared = [object]::ReferenceEquals($this, $PSSettings.Invocation.This)
                    CmdletShared = [object]::ReferenceEquals($PSCmdlet, $PSSettings.Invocation.Cmdlet)
                    BoundParametersShared = [object]::ReferenceEquals($PSBoundParameters, $PSSettings.Invocation.BoundParameters)
                    InvocationShared = [object]::ReferenceEquals($MyInvocation, $PSSettings.Invocation.Info)
                    ScriptRootEqual = [object]::Equals($PSScriptRoot, $PSSettings.Invocation.ScriptRoot)
                    CommandPathEqual = [object]::Equals($PSCommandPath, $PSSettings.Invocation.CommandPath)
                    ForEachShared = $foreachShared
                    SwitchShared = $switchShared
                    MatchesShared = [object]::ReferenceEquals($Matches, $PSSettings.Invocation.Matches)
                    ErrorsShared = $errorShared
                    StackTraceEqual = [object]::Equals($StackTrace, $PSSettings.Diagnostics.StackTrace)
                    LastExitCodeEqual = [object]::Equals($LASTEXITCODE, $PSSettings.LastExitCode)
                    HostShared = [object]::ReferenceEquals($Host, $PSSettings.Session.Host)
                    EngineIntrinsicsShared = [object]::ReferenceEquals($ExecutionContext, $PSSettings.Session.ExecutionContext)
                    HomeEqual = [object]::Equals($HOME, $PSSettings.Session.Home)
                    CurrentLocationShared = [object]::ReferenceEquals($PWD, $PSSettings.Session.CurrentLocation)
                    ProfileEqual = [object]::Equals($PROFILE, $PSSettings.Session.Profile)
                    NestedPromptEqual = [object]::Equals($NestedPromptLevel, $PSSettings.Session.NestedPromptLevel)
                    CurrentlyExecutingCommandEqual = [object]::Equals($CurrentlyExecutingCommand, $PSSettings.Session.CurrentlyExecutingCommand)
                    ProcessIdEqual = [object]::Equals($PID, $PSSettings.Runtime.ProcessId)
                    InstallationDirectoryEqual = [object]::Equals($PSHOME, $PSSettings.Runtime.InstallationDirectory)
                    ShellIdEqual = [object]::Equals($ShellId, $PSSettings.Runtime.ShellId)
                    VersionTableShared = $versionShared
                    EditionEqual = [object]::Equals($PSEdition, $PSSettings.Runtime.Edition)
                    ExperimentalFeaturesShared = [object]::ReferenceEquals($EnabledExperimentalFeatures, $PSSettings.Runtime.EnabledExperimentalFeatures)
                    CultureEqual = [object]::Equals($PSCulture, $PSSettings.Runtime.Culture)
                    UICultureEqual = [object]::Equals($PSUICulture, $PSSettings.Runtime.UICulture)
                    IsWindowsEqual = [object]::Equals($IsWindows, $PSSettings.Runtime.IsWindows)
                    IsLinuxEqual = [object]::Equals($IsLinux, $PSSettings.Runtime.IsLinux)
                    IsMacOSEqual = [object]::Equals($IsMacOS, $PSSettings.Runtime.IsMacOS)
                    IsCoreCLREqual = [object]::Equals($IsCoreCLR, $PSSettings.Runtime.IsCoreCLR)
                    FirstTokenEqual = [object]::Equals($^, $PSSettings.History.FirstToken)
                    LastTokenEqual = [object]::Equals($$, $PSSettings.History.LastToken)
                    SenderInfoEqual = [object]::Equals($PSSenderInfo, $PSSettings.Remoting.SenderInfo)
                    DebugContextEqual = [object]::Equals($PSDebugContext, $PSSettings.Diagnostics.DebugContext)
                    EventRecordEqual = [object]::Equals($Event, $PSSettings.Event.Record)
                    EventSubscriberEqual = [object]::Equals($EventSubscriber, $PSSettings.Event.Subscriber)
                    EventArgumentsEqual = [object]::Equals($EventArgs, $PSSettings.Event.Arguments)
                    EventSenderEqual = [object]::Equals($Sender, $PSSettings.Event.Sender)
                    TrueEqual = $PSSettings.Constants.True -eq $true
                    FalseEqual = $PSSettings.Constants.False -eq $false
                    NullEqual = $null -eq $PSSettings.Constants.Null
                    LastStatementSuccessType = $PSSettings.LastStatementSuccessful.GetType().FullName
                    LastStatementSuccessValue = $PSSettings.LastStatementSuccessful
                }
            }
            Get-ScopedRuntimeIdentity -Name probe
            """);

        foreach (string name in new[]
        {
            "ArgumentsShared", "InputShared", "ThisShared", "CmdletShared", "BoundParametersShared", "InvocationShared",
            "ScriptRootEqual", "CommandPathEqual", "ForEachShared", "SwitchShared", "MatchesShared",
            "ErrorsShared", "StackTraceEqual", "LastExitCodeEqual", "HostShared", "EngineIntrinsicsShared",
            "HomeEqual", "CurrentLocationShared", "ProfileEqual", "NestedPromptEqual", "CurrentlyExecutingCommandEqual",
            "ProcessIdEqual", "InstallationDirectoryEqual", "ShellIdEqual", "VersionTableShared", "EditionEqual",
            "ExperimentalFeaturesShared", "CultureEqual", "UICultureEqual", "IsWindowsEqual", "IsLinuxEqual",
            "IsMacOSEqual", "IsCoreCLREqual", "FirstTokenEqual", "LastTokenEqual", "SenderInfoEqual",
            "DebugContextEqual", "EventRecordEqual", "EventSubscriberEqual", "EventArgumentsEqual", "EventSenderEqual",
            "TrueEqual", "FalseEqual", "NullEqual",
        })
        {
            Assert.True(Boolean(result, name), $"Facade member did not match legacy value: {name}");
        }

        Assert.Equal(typeof(bool).FullName, result.Properties["LastStatementSuccessType"].Value);
        Assert.IsType<bool>(result.Properties["LastStatementSuccessValue"].Value);
    }

    [Fact]
    public void RuntimeWritesUseTargetScopesAndPreserveReferenceMutation()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;

        var result = InvokeSingle(shell, """
            $null = $global:LASTEXITCODE = 10
            $null = $globalView = $PSSettings.AtScope('Global')
            $null = $globalView.LastExitCode = 20
            $null = $globalValue = $global:LASTEXITCODE

            $null = $scriptModule = New-Module {}
            $null = $scriptResult = & $scriptModule {
                $null = $scriptView = $PSSettings.AtScope('Script')
                $null = $scriptView.LastExitCode = 30
                [pscustomobject]@{
                    ScriptValue = $script:LASTEXITCODE
                    GlobalValue = $global:LASTEXITCODE
                }
            }

            $null = $childResult = & {
                $null = $localView = $PSSettings.AtScope('Local')
                $null = $localView.LastExitCode = 40
                $null = $localValue = $LASTEXITCODE
                $null = $PSSettings.LastExitCode = 50
                $null = $unboundValue = $LASTEXITCODE

                $null = $hostWriteDenied = $false
                try { $localView.Session.Host = $null } catch { $null = $hostWriteDenied = $true }

                [pscustomobject]@{
                    LocalValue = $localValue
                    UnboundValue = $unboundValue
                    HostWriteDenied = $hostWriteDenied
                }
            }

            [pscustomobject]@{
                GlobalValue = $globalValue
                ScriptResult = $scriptResult
                GlobalAfterChild = $global:LASTEXITCODE
                ChildResult = $childResult
            }
            """);

        Assert.Equal(20, result.Properties["GlobalValue"].Value);
        var scriptResult = Assert.IsType<PSObject>(result.Properties["ScriptResult"].Value);
        Assert.Equal(30, scriptResult.Properties["ScriptValue"].Value);
        Assert.Equal(20, scriptResult.Properties["GlobalValue"].Value);
        Assert.Equal(20, result.Properties["GlobalAfterChild"].Value);
        var childResult = Assert.IsType<PSObject>(result.Properties["ChildResult"].Value);
        Assert.Equal(40, childResult.Properties["LocalValue"].Value);
        Assert.Equal(50, childResult.Properties["UnboundValue"].Value);
        Assert.True(Boolean(childResult, "HostWriteDenied"));

        // A nested object returned by a runtime variable stays shared; it is not a configuration COW value.
        var mutation = InvokeSingle(shell, """
            $null = $values = $PSSettings.Diagnostics.Errors
            $null = $marker = 'runtime-no-cow-marker'
            $values.Add($marker) | Out-Null
            $null = $errorShared = $Error.Contains($marker) -and [object]::ReferenceEquals($values, $Error)
            $values.Remove($marker) | Out-Null

            $null = $table = $PSSettings.Runtime.VersionTable
            $null = $table['ScopedRuntimeNoCowMarker'] = 'live'
            $null = $versionShared = [object]::ReferenceEquals($table, $PSVersionTable) -and $PSVersionTable.ContainsKey('ScopedRuntimeNoCowMarker')
            $table.Remove('ScopedRuntimeNoCowMarker') | Out-Null

            [pscustomobject]@{ ErrorShared = $errorShared; VersionShared = $versionShared }
            """);
        Assert.True(Boolean(mutation, "ErrorShared"));
        Assert.True(Boolean(mutation, "VersionShared"));
    }

    [Fact]
    public void TupleResolutionTracksNestedAndDottedInvocationFrames()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"scoped-runtime-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(scriptPath, """
                [pscustomobject]@{
                    ArgumentsShared = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
                BoundArgumentsShared = [object]::ReferenceEquals($args, $PSSettings.AtScope('Local').Invocation.Arguments)
                    Arguments = [string]::Join(',', $PSSettings.Invocation.Arguments)
                    InvocationShared = [object]::ReferenceEquals($MyInvocation, $PSSettings.Invocation.Info)
                    RootEqual = [object]::Equals($PSScriptRoot, $PSSettings.Invocation.ScriptRoot)
                    PathEqual = [object]::Equals($PSCommandPath, $PSSettings.Invocation.CommandPath)
                }
                """);

            using var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            using var shell = PowerShell.Create();
            shell.Runspace = runspace;
            string quotedPath = "'" + scriptPath.Replace("'", "''", StringComparison.Ordinal) + "'";
            var result = InvokeSingle(shell, $$"""
                function Get-ScopedRuntimeNestedTuple {
                    [pscustomobject]@{
                        ArgumentsShared = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
                        BoundArgumentsShared = [object]::ReferenceEquals($args, $PSSettings.AtScope('Local').Invocation.Arguments)
                        Arguments = [string]::Join(',', $PSSettings.Invocation.Arguments)
                        InvocationShared = [object]::ReferenceEquals($MyInvocation, $PSSettings.Invocation.Info)
                    }
                }

                function Get-ScopedRuntimeOuterTuple {
                    $null = $before = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
                    $null = $beforeArgs = [string]::Join(',', $PSSettings.Invocation.Arguments)
                    $null = $inner = Get-ScopedRuntimeNestedTuple inner-one inner-two
                    [pscustomobject]@{
                        BeforeShared = $before
                        BeforeArgs = $beforeArgs
                        AfterShared = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
                        AfterArgs = [string]::Join(',', $PSSettings.Invocation.Arguments)
                        Inner = $inner
                    }
                }

                $null = $outer = Get-ScopedRuntimeOuterTuple outer
                $null = $dotted = . {{quotedPath}} dotted-one dotted-two
                [pscustomobject]@{ Outer = $outer; Dotted = $dotted }
                """);

            var outer = Assert.IsType<PSObject>(result.Properties["Outer"].Value);
            Assert.True(Boolean(outer, "BeforeShared"));
            Assert.True(Boolean(outer, "AfterShared"));
            Assert.Equal("outer", outer.Properties["BeforeArgs"].Value);
            Assert.Equal("outer", outer.Properties["AfterArgs"].Value);

            var inner = Assert.IsType<PSObject>(outer.Properties["Inner"].Value);
            Assert.True(Boolean(inner, "ArgumentsShared"));
            Assert.True(Boolean(inner, "BoundArgumentsShared"));
            Assert.Equal("inner-one,inner-two", inner.Properties["Arguments"].Value);
            Assert.True(Boolean(inner, "InvocationShared"));

            var dotted = Assert.IsType<PSObject>(result.Properties["Dotted"].Value);
            Assert.True(Boolean(dotted, "ArgumentsShared"));
            Assert.True(Boolean(dotted, "BoundArgumentsShared"));
            Assert.Equal("dotted-one,dotted-two", dotted.Properties["Arguments"].Value);
            Assert.True(Boolean(dotted, "InvocationShared"));
            Assert.True(Boolean(dotted, "RootEqual"));
            Assert.True(Boolean(dotted, "PathEqual"));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void RuntimeViewsRespectBoundScopeLifetimeAndRunspaceOwnership()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        using var firstRunspace = RunspaceFactory.CreateRunspace();
        firstRunspace.Open();
        using var firstShell = PowerShell.Create();
        firstShell.Runspace = firstRunspace;

        var expiredFacade = Assert.Single(firstShell.AddScript("& { $PSSettings.AtScope('Local') }").Invoke<PSScopedPropertyFacade>());
        Assert.Empty(firstShell.Streams.Error);
        firstRunspace.SessionStateProxy.SetVariable("runtimeView", expiredFacade);
        firstShell.Commands.Clear();
        var expiredMessage = Assert.Single(firstShell.AddScript("try { $runtimeView.Invocation.Arguments } catch { $_.Exception.GetBaseException().Message }").Invoke<string>());
        Assert.Equal(SessionStateStrings.ExpiredScopedSettingScope, expiredMessage);

        var liveBoundFacade = Assert.Single(firstShell.AddScript("$PSSettings.AtScope('Global')").Invoke<PSScopedPropertyFacade>());
        Assert.Empty(firstShell.Streams.Error);
        using var secondRunspace = RunspaceFactory.CreateRunspace();
        secondRunspace.Open();
        secondRunspace.SessionStateProxy.SetVariable("runtimeView", liveBoundFacade);
        using var secondShell = PowerShell.Create();
        secondShell.Runspace = secondRunspace;
        var foreignMessage = Assert.Single(secondShell.AddScript("try { $runtimeView.Session.Host } catch { $_.Exception.GetBaseException().Message }").Invoke<string>());
        Assert.Equal(SessionStateStrings.ForeignScopedSettingScope, foreignMessage);
    }

    [Fact]
    public void MissingAndExplicitNullRuntimeVariablesFollowStrictModeAndInterpolation()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;

        var result = InvokeSingle(shell, """
            Remove-Variable PROFILE -Scope Global -Force -ErrorAction Ignore
            Remove-Variable LASTEXITCODE -Scope Global -Force -ErrorAction Ignore
            Set-StrictMode -Off
            $null = $profileIsNullWhenMissing = $null -eq $PSSettings.Session.Profile
            $null = $exitIsNullWhenMissing = $null -eq $PSSettings.LastExitCode

            Set-StrictMode -Version 1
            $null = $strictOneDirectThrows = $false
            try { $null = $PSSettings.Session.Profile } catch { $null = $strictOneDirectThrows = $true }
            $null = $strictOneInterpolation = "$($PSSettings.Session.Profile)"
            $null = $legacyCompound = & {
                Set-StrictMode -Version 1
                $null = $output = "$($PROFILE += 'x')"
                [pscustomobject]@{ Output = $output; Value = $PROFILE }
            }
            $null = $capturedSession = $PSSettings.Session
            $null = $runtimeDirectCompound = & {
                Set-StrictMode -Version 1
                $null = $output = "$($PSSettings.Session.Profile += 'x')"
                [pscustomobject]@{ Output = $output; Value = $PROFILE }
            }
            $null = $runtimeCapturedCompound = & {
                Set-StrictMode -Version 1
                $null = $output = "$($capturedSession.Profile += 'x')"
                [pscustomobject]@{ Output = $output; Value = $PROFILE }
            }

            $null = $expiredFacade = & { $PSSettings.AtScope('Local') }
            $null = $expiredInterpolationThrows = $false
            try { $null = "$($expiredFacade.Session.Host)" } catch { $null = $expiredInterpolationThrows = $true }

            Set-StrictMode -Version 2
            $null = $strictTwoDirectThrows = $false
            try { $null = $PSSettings.Session.Profile } catch { $null = $strictTwoDirectThrows = $true; $runtimeErrorId = $_.FullyQualifiedErrorId }
            try { $null = $PROFILE } catch { $legacyErrorId = $_.FullyQualifiedErrorId }
            $null = $strictTwoInterpolationThrows = $false
            try { $null = "$($PSSettings.Session.Profile)" } catch { $null = $strictTwoInterpolationThrows = $true }
            $null = $strictTwoLegacyCompoundThrows = $false
            try { $null = "$($PROFILE += 'x')" } catch { $null = $strictTwoLegacyCompoundThrows = $true }
            $null = $strictTwoRuntimeCompoundThrows = $false
            try { $null = "$($PSSettings.Session.Profile += 'x')" } catch { $null = $strictTwoRuntimeCompoundThrows = $true }
            $null = $strictTwoMissingExitThrows = $false
            try { $null = $PSSettings.LastExitCode } catch { $null = $strictTwoMissingExitThrows = $true }

            $null = $global:PROFILE = $null
            $null = $global:LASTEXITCODE = $null
            $null = $explicitNullDoesNotThrow = $true
            try { $null = $profileNull = $PSSettings.Session.Profile; $null = $exitNull = $PSSettings.LastExitCode } catch { $null = $explicitNullDoesNotThrow = $false }

            [pscustomobject]@{
                ProfileIsNullWhenMissing = $profileIsNullWhenMissing
                ExitIsNullWhenMissing = $exitIsNullWhenMissing
                StrictOneDirectThrows = $strictOneDirectThrows
                StrictOneInterpolation = $strictOneInterpolation
                LegacyCompoundOutput = $legacyCompound.Output
                LegacyCompoundValue = $legacyCompound.Value
                RuntimeDirectCompoundOutput = $runtimeDirectCompound.Output
                RuntimeDirectCompoundValue = $runtimeDirectCompound.Value
                RuntimeCapturedCompoundOutput = $runtimeCapturedCompound.Output
                RuntimeCapturedCompoundValue = $runtimeCapturedCompound.Value
                ExpiredInterpolationThrows = $expiredInterpolationThrows
                StrictTwoDirectThrows = $strictTwoDirectThrows
                RuntimeErrorId = $runtimeErrorId
                LegacyErrorId = $legacyErrorId
                StrictTwoInterpolationThrows = $strictTwoInterpolationThrows
                StrictTwoLegacyCompoundThrows = $strictTwoLegacyCompoundThrows
                StrictTwoRuntimeCompoundThrows = $strictTwoRuntimeCompoundThrows
                StrictTwoMissingExitThrows = $strictTwoMissingExitThrows
                ExplicitNullDoesNotThrow = $explicitNullDoesNotThrow
                ProfileExplicitNull = $null -eq $profileNull
                ExitExplicitNull = $null -eq $exitNull
            }
            """);

        Assert.True(Boolean(result, "ProfileIsNullWhenMissing"));
        Assert.True(Boolean(result, "ExitIsNullWhenMissing"));
        Assert.True(Boolean(result, "StrictOneDirectThrows"));
        Assert.Equal(string.Empty, result.Properties["StrictOneInterpolation"].Value);
        Assert.Equal(result.Properties["LegacyCompoundOutput"].Value, result.Properties["RuntimeDirectCompoundOutput"].Value);
        Assert.Equal(result.Properties["LegacyCompoundValue"].Value, result.Properties["RuntimeDirectCompoundValue"].Value);
        Assert.Equal(result.Properties["LegacyCompoundOutput"].Value, result.Properties["RuntimeCapturedCompoundOutput"].Value);
        Assert.Equal(result.Properties["LegacyCompoundValue"].Value, result.Properties["RuntimeCapturedCompoundValue"].Value);
        Assert.True(Boolean(result, "ExpiredInterpolationThrows"));
        Assert.True(Boolean(result, "StrictTwoDirectThrows"));
        Assert.Equal("VariableIsUndefined", result.Properties["RuntimeErrorId"].Value);
        Assert.Equal(result.Properties["LegacyErrorId"].Value, result.Properties["RuntimeErrorId"].Value);
        Assert.True(Boolean(result, "StrictTwoInterpolationThrows"));
        Assert.True(Boolean(result, "StrictTwoLegacyCompoundThrows"));
        Assert.True(Boolean(result, "StrictTwoRuntimeCompoundThrows"));
        Assert.True(Boolean(result, "StrictTwoMissingExitThrows"));
        Assert.True(Boolean(result, "ExplicitNullDoesNotThrow"));
        Assert.True(Boolean(result, "ProfileExplicitNull"));
        Assert.True(Boolean(result, "ExitExplicitNull"));
    }

    [Fact]
    public void RuntimeVariableAccessHonorsPrivateModuleBindingsAndConstrainedLanguagePermissions()
    {
        if (!ExperimentalFeature.PSScopedProperties)
        {
            return;
        }

        using (var runspace = RunspaceFactory.CreateRunspace())
        {
            runspace.Open();
            using var shell = PowerShell.Create();
            shell.Runspace = runspace;
            var result = InvokeSingle(shell, """
                Remove-Variable PROFILE -Scope Global -Force -ErrorAction Ignore
                $null = $module = New-Module {}
                $null = $moduleOutput = & $module {
                    New-Variable -Name PROFILE -Value 'private-module-profile' -Option Private
                    $PSSettings.Session.Profile
                }
                [pscustomobject]@{
                    ModuleOutput = $moduleOutput
                    OutsideIsNull = $null -eq $PSSettings.Session.Profile
                }
                """);

            Assert.Equal("private-module-profile", result.Properties["ModuleOutput"].Value);
            Assert.True(Boolean(result, "OutsideIsNull"));
        }

        var initialState = InitialSessionState.CreateDefault2();
        initialState.LanguageMode = PSLanguageMode.ConstrainedLanguage;
        using var constrainedRunspace = RunspaceFactory.CreateRunspace(initialState);
        constrainedRunspace.Open();
        using var constrainedShell = PowerShell.Create();
        constrainedShell.Runspace = constrainedRunspace;
        constrainedShell.Commands.Clear();
        var constrainedResult = Assert.Single(constrainedShell.AddScript("""
            $global:LASTEXITCODE = 60
            $null = $facadeWriteDenied = $false
            try { $PSSettings.LastExitCode = 61 } catch { $null = $facadeWriteDenied = $true }
            $null = $readOnlyWriteDenied = $false
            try { $PSSettings.Session.Host = $null } catch { $null = $readOnlyWriteDenied = $true }
            "$facadeWriteDenied|$readOnlyWriteDenied|$LASTEXITCODE"
            """).Invoke<string>());

        Assert.Equal("True|True|60", constrainedResult);
    }
}
