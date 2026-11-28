# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

Describe 'PSScopedProperties in an enabled test host' -Tag 'CI' {
    $skipFeatureTests = -not [ExperimentalFeature]::IsEnabled('PSScopedProperties')
    $preferenceCases = @(
        @{ PreferenceName = 'Error'; VariableName = 'ErrorActionPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Progress'; VariableName = 'ProgressPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Warning'; VariableName = 'WarningPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Information'; VariableName = 'InformationPreference'; DefaultPreference = 'SilentlyContinue' }
        @{ PreferenceName = 'Verbose'; VariableName = 'VerbosePreference'; DefaultPreference = 'SilentlyContinue' }
        @{ PreferenceName = 'Debug'; VariableName = 'DebugPreference'; DefaultPreference = 'SilentlyContinue' }
    )

    $suspendCases = foreach ($preferenceCase in $preferenceCases) {
        $preferenceCase + @{ Value = [System.Management.Automation.ActionPreference]::Suspend; ValueKind = 'an enum' }
        $preferenceCase + @{ Value = 'Suspend'; ValueKind = 'a string' }
    }

    It 'exposes the constant AllScope facade in the test host' -Skip:$skipFeatureTests {
        [ExperimentalFeature]::IsEnabled('PSScopedProperties') | Should -BeTrue
        (Get-Variable PSSettings).Options | Should -Be ([System.Management.Automation.ScopedItemOptions]'Constant, AllScope')
        $PSSettings.Style.Progress.View | Should -BeOfType ([System.Management.Automation.ProgressView])
    }

    It 'exposes the typed scalar preferences from the test host' -Skip:$skipFeatureTests {
        # CI suppresses progress before starting Pester; the facade must reflect that preference.
        $PSSettings.ActionPreference.Progress | Should -BeOfType ([System.Management.Automation.ActionPreference])
        $PSSettings.ActionPreference.Progress | Should -Be $ProgressPreference
        $PSSettings.ShouldProcess.Confirm | Should -Be ([System.Management.Automation.ConfirmImpact]::High)
        $PSSettings.ShouldProcess.WhatIf | Should -BeOfType ([bool])
        $PSSettings.ShouldProcess.WhatIf | Should -BeFalse
        $ConfirmPreference | Should -Be ([System.Management.Automation.ConfirmImpact]::High)
        $WhatIfPreference | Should -BeOfType ([bool])
        $WhatIfPreference | Should -BeFalse
    }

    It 'normalizes native command error preference values to bool and controls non-zero child exits' -Skip:$skipFeatureTests {
        $nativeCommand = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
        $PSSettings.NativeCommand.UseErrorActionPreference = 'Yeah'
        $PSSettings.NativeCommand.UseErrorActionPreference | Should -BeOfType ([bool])
        $PSSettings.NativeCommand.UseErrorActionPreference | Should -BeTrue

        $ErrorActionPreference = 'Stop'
        $nativeError = try {
            & $nativeCommand -NoLogo -NoProfile -Command 'exit 7'
            $null
        }
        catch {
            $_
        }
        $nativeError | Should -Not -BeNullOrEmpty
        $nativeError.FullyQualifiedErrorId | Should -Be 'ProgramExitedWithNonZeroCode'

        $PSSettings.NativeCommand.UseErrorActionPreference = 0
        $PSSettings.NativeCommand.UseErrorActionPreference | Should -BeOfType ([bool])
        $PSSettings.NativeCommand.UseErrorActionPreference | Should -BeFalse
        $ErrorActionPreference = 'Continue'
        & $nativeCommand -NoLogo -NoProfile -Command 'exit 7'
        $LASTEXITCODE | Should -Be 7
    }

    It 'exposes the typed <PreferenceName> default in a fresh runspace' -TestCases $preferenceCases -Skip:$skipFeatureTests {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $shell = [powershell]::Create()
        try {
            $null = $shell.AddScript({
                param($name, $variableName)
                $PSSettings.ActionPreference.$name
                (Get-Variable -Name $variableName).Value
            }).AddArgument($PreferenceName).AddArgument($VariableName)
            $values = $shell.Invoke()
            $shell.HadErrors | Should -BeFalse
            $values.Count | Should -Be 2
            foreach ($value in $values) {
                $value | Should -BeOfType ([System.Management.Automation.ActionPreference])
                $value | Should -Be ([System.Management.Automation.ActionPreference]$DefaultPreference)
            }
        } finally {
            $shell.Dispose()
        }
    }

    It 'shares typed values between the <PreferenceName> facade and legacy variable' -TestCases $preferenceCases -Skip:$skipFeatureTests {
        param($PreferenceName, $VariableName)

        & {
            Set-Variable -Name $VariableName -Value 'sIlEnTlYcOnTiNuE'
            $PSSettings.ActionPreference.$PreferenceName | Should -Be ([System.Management.Automation.ActionPreference]::SilentlyContinue)
            (Get-Variable -Name $VariableName).Value | Should -BeOfType ([System.Management.Automation.ActionPreference])

            $PSSettings.ActionPreference.$PreferenceName = 'Continue'
            (Get-Variable -Name $VariableName).Value | Should -Be ([System.Management.Automation.ActionPreference]::Continue)

            { $PSSettings.ActionPreference.$PreferenceName = 'invalid preference' } | Should -Throw
            (Get-Variable -Name $VariableName).Value | Should -Be ([System.Management.Automation.ActionPreference]::Continue)
        }
    }

    It 'resets a child <PreferenceName> preference to the inherited value' -TestCases $preferenceCases -Skip:$skipFeatureTests {
        param($PreferenceName, $VariableName)

        & {
            $PSSettings.ActionPreference.$PreferenceName = 'Continue'
            & {
                Set-Variable -Name $VariableName -Value 'SilentlyContinue' -Scope Local
                $PSSettings.ActionPreference.$PreferenceName | Should -Be ([System.Management.Automation.ActionPreference]::SilentlyContinue)
                $PSSettings.Reset("aCtIoNpReFeReNcE.$PreferenceName")
                $PSSettings.ActionPreference.$PreferenceName | Should -Be ([System.Management.Automation.ActionPreference]::Continue)
                Get-Variable -Name $VariableName -Scope Local -ErrorAction Ignore | Should -BeNullOrEmpty
            }
            (Get-Variable -Name $VariableName).Value | Should -Be ([System.Management.Automation.ActionPreference]::Continue)
        }
    }

    It 'rejects <PreferenceName> Suspend supplied as <ValueKind> during assignment' -TestCases $suspendCases -Skip:$skipFeatureTests {
        param($PreferenceName, $VariableName, $Value, $ValueKind)

        & {
            $originalPreference = [System.Management.Automation.ActionPreference]::SilentlyContinue
            Set-Variable -Name $VariableName -Value $originalPreference -Scope Local

            $reachedCommand = $false
            $record = try {
                Set-Variable -Name $VariableName -Value $Value -Scope Local -ErrorAction Stop
                $reachedCommand = $true
                & "Write-$PreferenceName" 'unexpected stream message' *>$null
            } catch {
                $_
            }

            $record.FullyQualifiedErrorId | Should -BeExactly 'InvalidActionPreference,Microsoft.PowerShell.Commands.SetVariableCommand'
            $record.CategoryInfo.Reason | Should -BeExactly 'PSInvalidCastException'
            $reachedCommand | Should -BeFalse
            (Get-Variable -Name $VariableName -Scope Local).Value | Should -BeExactly $originalPreference
            $PSSettings.ActionPreference.$PreferenceName | Should -BeExactly $originalPreference
        }
    }

    It 'isolates progress overrides and shares them with PSStyle' -Skip:$skipFeatureTests {
        & {
            $PSSettings.Style.Progress.View = 'Minimal'
            & {
                $PSStyle.Progress.View = 'Classic'
                $PSSettings.Style.Progress.View | Should -Be ([System.Management.Automation.ProgressView]::Classic)
                $PSSettings.Reset('Style.Progress.View')
                $PSStyle.Progress.View | Should -Be ([System.Management.Automation.ProgressView]::Minimal)
            }
            $PSSettings.Style.Progress.View | Should -Be ([System.Management.Automation.ProgressView]::Minimal)
        }
    }
}
