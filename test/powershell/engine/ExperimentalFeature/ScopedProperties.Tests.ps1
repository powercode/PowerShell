# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'These isolated child-process tests explicitly verify global preference bindings and debugger callbacks.')]
param()

Describe 'PSScopedProperties' -Tag 'CI' {
    $preferenceCases = @(
        @{ PreferenceName = 'Error'; VariableName = 'ErrorActionPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Progress'; VariableName = 'ProgressPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Warning'; VariableName = 'WarningPreference'; DefaultPreference = 'Continue' }
        @{ PreferenceName = 'Information'; VariableName = 'InformationPreference'; DefaultPreference = 'SilentlyContinue' }
        @{ PreferenceName = 'Verbose'; VariableName = 'VerbosePreference'; DefaultPreference = 'SilentlyContinue' }
        @{ PreferenceName = 'Debug'; VariableName = 'DebugPreference'; DefaultPreference = 'SilentlyContinue' }
    )

    $streamCases = @($preferenceCases | Where-Object { $_.PreferenceName -notin 'Error', 'Progress' })

    BeforeAll {
        $enabledConfig = Join-Path $TestDrive 'enabled.json'
        $disabledConfig = Join-Path $TestDrive 'disabled.json'
        '{"ExperimentalFeatures":["PSScopedProperties"]}' | Set-Content -LiteralPath $enabledConfig
        '{"ExperimentalFeatures":[]}' | Set-Content -LiteralPath $disabledConfig
        $pwshName = if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }
        $pwsh = Join-Path $PSHOME $pwshName
        $hookSource = Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets/ScopedPropertiesConfigStartupHook.cs') -Destination $TestDrive -PassThru
        $hookAssembly = Join-Path $TestDrive 'ScopedPropertiesConfigStartupHook.dll'
        Add-Type -Path $hookSource.FullName -OutputAssembly $hookAssembly

        function Invoke-ScopedPropertiesTest {
            param(
                [scriptblock] $Script,
                [switch] $Disabled,
                [string] $PreferenceName,
                [string] $VariableName
            )

            $scriptText = $Script.ToString()
            if ($PreferenceName) {
                # Keep actual variable syntax in the child script to exercise the compiler and debugger paths.
                $scriptText = $scriptText.Replace('__PREFERENCE__', $PreferenceName).Replace('__PREFERENCEVARIABLE__', $VariableName)
            }
            $config = if ($Disabled) { $disabledConfig } else { $enabledConfig }
            $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($scriptText))
            $originalHooks = $env:DOTNET_STARTUP_HOOKS
            try {
                $env:DOTNET_STARTUP_HOOKS = $hookAssembly
                $output = & $pwsh -NoLogo -NoProfile -NonInteractive -SettingsFile $config -OutputFormat Text -EncodedCommand $encodedCommand 2>&1
            } finally {
                $env:DOTNET_STARTUP_HOOKS = $originalHooks
            }
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
            @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }) | Should -BeNullOrEmpty
            $output
        }
    }

    It 'leaves PSSettings available as an ordinary variable when disabled' {
        $result = Invoke-ScopedPropertiesTest -Disabled {
            $null -eq (Get-Variable PSSettings -ErrorAction Ignore)
            . ([scriptblock]::Create('$global:PSSettings = "user value"; $global:PSSettings'))
            $PSStyle.Progress.View = 'Classic'
            & {
                $PSStyle.Progress.View = 'Minimal'
            }
            $PSStyle.Progress.View
        }
        $result | Should -Be @('True', 'user value', 'Minimal')
    }

    It 'leaves PS available as an ordinary variable when enabled' {
        $result = Invoke-ScopedPropertiesTest {
            $ps = [powershell]::Create()
            try {
                $ps.AddScript('42').Invoke()
            } finally {
                $ps.Dispose()
            }
            $script:ps = 'script value'
            $script:ps
            [ExperimentalFeature]::IsEnabled('PSScopedProperties')
            (Get-Variable PSSettings).Options
        }
        $result | Should -Be @('42', 'script value', 'True', 'Constant, AllScope')
    }

    It 'retains legacy error preferences and untyped child shadows when disabled' {
        $result = Invoke-ScopedPropertiesTest -Disabled {
            $EnabledExperimentalFeatures.Contains('PSScopedProperties')
            $ErrorActionPreference = 'sToP'
            "$($ErrorActionPreference.GetType().Name)/$ErrorActionPreference"
            try { $ErrorActionPreference = 'invalid preference' } catch { 'built-in rejected write' }
            $ErrorActionPreference
            & {
                $ErrorActionPreference = 'cOnTiNuE'
                "$($ErrorActionPreference.GetType().Name)/$ErrorActionPreference"
                $ErrorActionPreference = 'invalid preference'
                $ErrorActionPreference
                Write-Error 'expected' -ErrorAction Ignore
                'ignored'
            }
            function Invoke-DisabledAdvancedPreference {
                [CmdletBinding()]
                param()
                "$($ErrorActionPreference.GetType().Name)/$ErrorActionPreference"
                Write-Error 'expected' -ErrorAction Ignore
                'ignored'
            }
            Invoke-DisabledAdvancedPreference -ErrorAction SilentlyContinue
            $ErrorActionPreference
        }
        $result | Should -BeExactly @(
            'False', 'ActionPreference/Stop', 'built-in rejected write', 'Stop',
            'String/cOnTiNuE', 'invalid preference', 'ignored', 'ActionPreference/SilentlyContinue', 'ignored', 'Stop'
        )
    }

    It 'retains typed globals and untyped child shadows for <PreferenceName> when disabled' -TestCases $streamCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -Disabled -PreferenceName $PreferenceName -VariableName $VariableName {
            $EnabledExperimentalFeatures.Contains('PSScopedProperties')
            "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__"
            $__PREFERENCEVARIABLE__ = 'sToP'
            "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__"
            try { $__PREFERENCEVARIABLE__ = 'invalid preference' } catch { 'global conversion rejected' }
            try { $__PREFERENCEVARIABLE__ = 'Suspend' } catch { 'global validation rejected' }
            $__PREFERENCEVARIABLE__
            & {
                $__PREFERENCEVARIABLE__ = 'cOnTiNuE'
                "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__"
                $__PREFERENCEVARIABLE__ = 'Suspend'
                $__PREFERENCEVARIABLE__
                $__PREFERENCEVARIABLE__ = 'invalid preference'
                $__PREFERENCEVARIABLE__
            }
            $__PREFERENCEVARIABLE__
        }
        $result | Should -BeExactly @(
            'False', "ActionPreference/$DefaultPreference", 'ActionPreference/Stop',
            'global conversion rejected', 'global validation rejected', 'Stop',
            'String/cOnTiNuE', 'Suspend', 'invalid preference', 'Stop'
        )
    }

    It 'registers a constant AllScope facade with read-only grouping properties' {
        $result = Invoke-ScopedPropertiesTest {
            $EnabledExperimentalFeatures.Contains('PSScopedProperties')
            (Get-Variable PSSettings).Options
            & { (Get-Variable PSSettings -Scope Local).Options }
            foreach ($name in 'ActionPreference', 'Style') {
                $PSSettings.GetType().GetProperty($name).CanWrite
            }
            $PSSettings.Style.GetType().GetProperty('Progress').CanWrite
            $PSSettings.ActionPreference.Error.GetType().Name
            $PSSettings.Style.Progress.View.GetType().Name
            try { Set-Variable PSSettings 'replacement' -Force -ErrorAction Stop } catch { $_.FullyQualifiedErrorId }
        }
        $result | Should -Be @(
            'True', 'Constant, AllScope', 'Constant, AllScope',
            'False', 'False', 'False', 'ActionPreference', 'ProgressView',
            'VariableNotWritable,Microsoft.PowerShell.Commands.SetVariableCommand'
        )
    }

    It 'shares scoped Progress and ShouldProcess preferences with their legacy variables' {
        $result = Invoke-ScopedPropertiesTest {
            "defaults/$($PSSettings.ActionPreference.Progress.GetType().Name)/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm.GetType().Name)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf.GetType().Name)/$($PSSettings.ShouldProcess.WhatIf)"
            "legacy/$($ProgressPreference.GetType().Name)/$ProgressPreference/$($ConfirmPreference.GetType().Name)/$ConfirmPreference/$($WhatIfPreference.GetType().Name)/$WhatIfPreference"

            $PSSettings.ActionPreference.Progress = 'SilentlyContinue'
            $PSSettings.ShouldProcess.Confirm = 'Low'
            $PSSettings.ShouldProcess.WhatIf = $true
            "variables/$ProgressPreference/$ConfirmPreference/$WhatIfPreference"

            & {
                $ProgressPreference = 'Stop'
                $ConfirmPreference = 'None'
                $WhatIfPreference = $false
                "facade/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
                $PSSettings.Reset('ActionPreference.Progress')
                $PSSettings.Reset('ShouldProcess.Confirm')
                $PSSettings.Reset('ShouldProcess.WhatIf')
                "reset/$ProgressPreference/$ConfirmPreference/$WhatIfPreference"
            }
            "outer/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"

            $PSSettings.ShouldProcess.WhatIf = [guid]::NewGuid()
            "whatif truthiness/$($PSSettings.ShouldProcess.WhatIf.GetType().Name)/$($PSSettings.ShouldProcess.WhatIf)"

            $before = $PSSettings.ShouldProcess.Confirm
            try { $PSSettings.ShouldProcess.Confirm = [guid]::NewGuid() } catch { "confirm rejected/$($PSSettings.ShouldProcess.Confirm)" }
            $before -eq $PSSettings.ShouldProcess.Confirm

            $globalView = $PSSettings.AtScope('Global')
            $globalView.ShouldProcess.Confirm = 'Low'
            $globalView.ShouldProcess.WhatIf = $true
            "global/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
            $globalView.Reset('ShouldProcess.Confirm')
            $globalView.Reset('ShouldProcess.WhatIf')
        }
        $result | Should -BeExactly @(
            'defaults/ActionPreference/Continue/ConfirmImpact/High/Boolean/False',
            'legacy/ActionPreference/Continue/ConfirmImpact/High/Boolean/False',
            'variables/SilentlyContinue/Low/True',
            'facade/Stop/None/False',
            'reset/SilentlyContinue/Low/True',
            'outer/SilentlyContinue/Low/True',
            'whatif truthiness/Boolean/True', 'confirm rejected/Low', 'True', 'global/Low/True'
        )
    }

    It 'binds explicitly named ConfirmPreference and WhatIfPreference parameters through scoped descriptors' {
        $result = Invoke-ScopedPropertiesTest {
            function Test-ExplicitShouldProcessPreferenceParameters {
                [CmdletBinding()]
                param(
                    [ValidateSet('Low', 'High')][System.Management.Automation.ConfirmImpact] $ConfirmPreference,
                    [bool] $WhatIfPreference
                )
                "parameters/$ConfirmPreference/$WhatIfPreference"
                "facade/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
            }

            $PSSettings.ShouldProcess.Confirm = 'High'
            $PSSettings.ShouldProcess.WhatIf = $false
            Test-ExplicitShouldProcessPreferenceParameters -ConfirmPreference Low -WhatIfPreference $true
            "caller/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
            try { Test-ExplicitShouldProcessPreferenceParameters -ConfirmPreference NotAnImpact -WhatIfPreference $true } catch { 'invalid ConfirmPreference rejected' }
            "after failure/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
        }
        $result | Should -BeExactly @(
            'parameters/Low/True', 'facade/Low/True', 'caller/High/False',
            'invalid ConfirmPreference rejected', 'after failure/High/False'
        )
    }

    It 'resets a local <PreferenceName> override created through either access path' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            & {
                $__PREFERENCEVARIABLE__ = 'Stop'
                $PSSettings.ActionPreference.__PREFERENCE__

                $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue'
                $__PREFERENCEVARIABLE__

                $PSSettings.Reset('aCtIoNpReFeReNcE.__PREFERENCE__')
                $PSSettings.ActionPreference.__PREFERENCE__
            }
            $PSSettings.ActionPreference.__PREFERENCE__
        }
        $result | Should -Be @('Stop', 'SilentlyContinue', 'Continue', 'Continue')
    }

    It 'inherits each leaf independently and resolves captured groups at each use' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Error = 'Continue'
            $PSSettings.Style.Progress.View = 'Minimal'
            $preferences = $PSSettings.ActionPreference
            $progress = $PSSettings.Style.Progress
            & {
                $ErrorActionPreference = 'Stop'
                "$($preferences.Error)/$($progress.View)"
                $progress.View = 'Classic'
                & {
                    $preferences.Error = 'SilentlyContinue'
                    "$ErrorActionPreference/$($PSStyle.Progress.View)"
                    $PSSettings.Reset('Style.Progress.View')
                    $PSSettings.Reset('ActionPreference.Error')
                    "$($PSSettings.ActionPreference.Error)/$($PSStyle.Progress.View)"
                }
                $PSSettings.Reset('ActionPreference.Error')
                "$($PSSettings.ActionPreference.Error)/$($PSStyle.Progress.View)"
                . { $PSStyle.Progress.View = 'Minimal' }
                $PSSettings.Style.Progress.View
            }
            "$($PSSettings.ActionPreference.Error)/$($PSStyle.Progress.View)"
        }
        $result | Should -Be @('Stop/Minimal', 'SilentlyContinue/Classic', 'Stop/Classic', 'Continue/Classic', 'Minimal', 'Continue/Minimal')
    }

    It 'uses typed conversion for <PreferenceName> global and child-scope assignments' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $__PREFERENCEVARIABLE__ = 'sToP'
            "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            try { $__PREFERENCEVARIABLE__ = 'invalid preference' } catch { 'built-in rejected write' }
            "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            & {
                try { $__PREFERENCEVARIABLE__ = 'invalid preference' } catch { 'initial child write rejected' }
                $null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Ignore)
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                $__PREFERENCEVARIABLE__ = 'sIlEnTlYcOnTiNuE'
                "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                foreach ($invalid in @($null, 500, 'invalid preference')) {
                    try { $__PREFERENCEVARIABLE__ = $invalid } catch { 'child write rejected' }
                    "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                }
                $__PREFERENCEVARIABLE__ = 2
                "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
            "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -BeExactly @(
            'ActionPreference/Stop/Stop', 'built-in rejected write', 'Stop/Stop',
            'initial child write rejected', 'True', 'Stop/Stop',
            'ActionPreference/SilentlyContinue/SilentlyContinue',
            'child write rejected', 'SilentlyContinue/SilentlyContinue',
            'child write rejected', 'SilentlyContinue/SilentlyContinue',
            'child write rejected', 'SilentlyContinue/SilentlyContinue',
            'ActionPreference/Continue/Continue', 'Stop/Stop'
        )
    }

    It 'normalizes <PreferenceName> variable cmdlet writes and resumes inheritance after removal' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            & {
                $created = New-Variable __PREFERENCEVARIABLE__ 'cOnTiNuE' -Scope Local -PassThru
                "$($created.Value.GetType().Name)/$($created.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                Set-Variable __PREFERENCEVARIABLE__ 0 -Scope Local
                $legacyValue = (Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Value
                "$($legacyValue.GetType().Name)/$legacyValue/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try { New-Variable __PREFERENCEVARIABLE__ 500 -Scope Local -Force -ErrorAction Stop } catch { 'replacement rejected' }
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                foreach ($invalid in @($null, 500, 'invalid preference')) {
                    try { Set-Variable __PREFERENCEVARIABLE__ $invalid -Scope Local -ErrorAction Stop } catch { 'set rejected' }
                    "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                }
                try { Clear-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Stop } catch { 'clear rejected' }
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"

                $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
                (Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Value.GetType().Name
                $PSSettings.ActionPreference.__PREFERENCE__
                Remove-Variable __PREFERENCEVARIABLE__ -Scope Local
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                $local:__PREFERENCEVARIABLE__ = 'silentlycontinue'
                "$($local:__PREFERENCEVARIABLE__.GetType().Name)/$($PSSettings.ActionPreference.__PREFERENCE__)/$global:__PREFERENCEVARIABLE__"
                $PSSettings.Reset('ActionPreference.__PREFERENCE__')
                $null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Ignore)
            }
        }
        $result | Should -BeExactly @(
            'ActionPreference/Continue/Continue', 'ActionPreference/SilentlyContinue/SilentlyContinue',
            'replacement rejected', 'SilentlyContinue/SilentlyContinue',
            'set rejected', 'SilentlyContinue/SilentlyContinue', 'set rejected', 'SilentlyContinue/SilentlyContinue',
            'set rejected', 'SilentlyContinue/SilentlyContinue', 'clear rejected', 'SilentlyContinue/SilentlyContinue',
            'ActionPreference', 'Continue', 'Stop/Stop', 'ActionPreference/SilentlyContinue/Stop', 'True'
        )
    }

    It 'preserves <PreferenceName> read-only and constant protections' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            foreach ($option in 'ReadOnly', 'Constant') {
                & {
                    New-Variable __PREFERENCEVARIABLE__ -Value 'Continue' -Option $option
                    try { $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop' } catch { 'write rejected' }
                    try { $PSSettings.Reset('ActionPreference.__PREFERENCE__') } catch { 'reset rejected' }
                    "$((Get-Variable __PREFERENCEVARIABLE__).Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                }
            }
            try { $PSSettings.ActionPreference.__PREFERENCE__ = 'bad action' } catch { 'invalid action rejected' }
            try { $PSSettings.ActionPreference.__PREFERENCE__ = 500 } catch { 'invalid action number rejected' }
        }
        $result | Should -Be @(
            'write rejected', 'reset rejected', 'ReadOnly/Continue',
            'write rejected', 'reset rejected', 'Constant/Continue',
            'invalid action rejected', 'invalid action number rejected'
        )
    }

    It 'rejects invalid progress view values' {
        $result = Invoke-ScopedPropertiesTest {
            try { $PSSettings.Style.Progress.View = 'bad view' } catch { 'invalid view rejected' }
            try { $PSSettings.Style.Progress.View = 500 } catch { 'invalid view number rejected' }
        }
        $result | Should -BeExactly @('invalid view rejected', 'invalid view number rejected')
    }

    It 'preserves <PreferenceName> metadata and value on failed forced writes' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            & {
                New-Variable __PREFERENCEVARIABLE__ 'Stop' -Option ReadOnly -Description 'original'
                try { Set-Variable __PREFERENCEVARIABLE__ 500 -Force -Description 'rejected' -ErrorAction Stop } catch { 'forced set rejected' }
                $retained = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                "$($retained.Options)/$($retained.Description)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try { Clear-Variable __PREFERENCEVARIABLE__ -Scope Local -Force -ErrorAction Stop } catch { 'forced clear rejected' }
                "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$__PREFERENCEVARIABLE__"
                Set-Variable __PREFERENCEVARIABLE__ 'silentlyContinue' -Force
                "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                Set-Variable __PREFERENCEVARIABLE__ 'Stop' -Force -Option None
                "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                Set-Variable __PREFERENCEVARIABLE__ 'SilentlyContinue' -Option ReadOnly
                "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try {
                    Set-Variable __PREFERENCEVARIABLE__ 'Stop' -Force -Option Constant -ErrorAction Stop
                } catch { 'constant option rejected' }
                "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try { Remove-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Stop } catch { 'remove rejected' }
                Remove-Variable __PREFERENCEVARIABLE__ -Scope Local -Force
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
        }
        $result | Should -BeExactly @(
            'forced set rejected', 'ReadOnly/original/Stop', 'forced clear rejected', 'ReadOnly/Stop',
            'ReadOnly/SilentlyContinue', 'None/Stop', 'ReadOnly/SilentlyContinue',
            'constant option rejected', 'ReadOnly/SilentlyContinue', 'remove rejected', 'Continue/Continue'
        )
    }

    It 'shares <PreferenceName> values with variable-provider item operations' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $path = 'Variable:__PREFERENCEVARIABLE__'
            Set-Item -LiteralPath $path -Value 'sToP'
            $value = (Get-Item -LiteralPath $path).Value
            "$($value.GetType().Name)/$value/$($PSSettings.ActionPreference.__PREFERENCE__)"
            try { Set-Item -LiteralPath $path -Value 500 -ErrorAction Stop } catch { 'provider set rejected' }
            try { Clear-Item -LiteralPath $path -ErrorAction Stop } catch { 'provider clear rejected' }
            "$((Get-Item -LiteralPath $path).Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"

            Remove-Item -LiteralPath $path
            "$(Test-Path -LiteralPath $path)/$($PSSettings.ActionPreference.__PREFERENCE__)"
            $created = New-Item -Path $path -Value 'silentlycontinue'
            "$($created.Value.GetType().Name)/$($created.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -BeExactly @(
            'ActionPreference/Stop/Stop', 'provider set rejected', 'provider clear rejected', 'Stop/Stop',
            "False/$DefaultPreference", 'ActionPreference/SilentlyContinue/SilentlyContinue'
        )
    }

    It 'updates retained <PreferenceName> variables through the provider and detaches them on removal' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            $path = 'Variable:__PREFERENCEVARIABLE__'
            $retained = Get-Item -LiteralPath $path

            Set-Content -LiteralPath $path -Value 'sIlEnTlYcOnTiNuE'
            "$($retained.Value.GetType().Name)/$($retained.Value)/$(Get-Content -LiteralPath $path)"
            try { Set-Content -LiteralPath $path -Value 500 -ErrorAction Stop } catch { 'invalid content rejected' }
            try { Clear-Content -LiteralPath $path -ErrorAction Stop } catch { 'clear content rejected' }
            try { Clear-Item -LiteralPath $path -ErrorAction Stop } catch { 'clear item rejected' }
            "$($retained.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"

            Remove-Item -LiteralPath $path
            "$($null -eq $retained.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)/$(Test-Path -LiteralPath $path)"
            $retained.Value = 'detached'
            "$($retained.Value)/$(Test-Path -LiteralPath $path)"
        }
        $result | Should -BeExactly @(
            'ActionPreference/SilentlyContinue/SilentlyContinue', 'invalid content rejected', 'clear content rejected', 'clear item rejected',
            'SilentlyContinue/SilentlyContinue', "True/$DefaultPreference/False", 'detached/False'
        )
    }

    It 'shares <PreferenceName> AllScope writes and removes only the target scope binding' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            & {
                New-Variable __PREFERENCEVARIABLE__ 'Continue' -Option AllScope
                $parent = $PSSettings.AtScope('Local')
                $retained = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                & {
                    $__PREFERENCEVARIABLE__ = 'SilentlyContinue'
                    $shared = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                    $shared.Description = 'shared'
                    "$($shared.Options)/$($parent.ActionPreference.__PREFERENCE__)/$($retained.Description)/$([object]::ReferenceEquals($retained, $shared))"
                    $PSSettings.Reset('ActionPreference.__PREFERENCE__')
                    $null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Ignore)
                    "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"

                    $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
                    "$((Get-Variable __PREFERENCEVARIABLE__ -Scope Local).Options)/$__PREFERENCEVARIABLE__/$($parent.ActionPreference.__PREFERENCE__)"
                }
                & {
                    Remove-Variable __PREFERENCEVARIABLE__ -Scope Local
                    $null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Ignore)
                    "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                }
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
            "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -BeExactly @(
            'AllScope/SilentlyContinue/shared/True', 'True', 'SilentlyContinue/SilentlyContinue',
            'None/Continue/SilentlyContinue', 'True', 'SilentlyContinue/SilentlyContinue',
            'SilentlyContinue/SilentlyContinue', 'Stop/Stop'
        )
    }

    It 'rejects assignments of Suspend for <PreferenceName>' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue'
            try { $__PREFERENCEVARIABLE__ = 'Suspend' } catch { $_.Exception.Message.Contains('Suspend') }
            try { $PSSettings.ActionPreference.__PREFERENCE__ = 'Suspend' } catch { $_.Exception.Message.Contains('Suspend') }
            "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -Be @('True', 'True', 'SilentlyContinue/SilentlyContinue')
    }

    It 'honors <PreferenceName> validation attributes and private visibility' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            & {
                [ValidateSet('Continue', 'Stop')]$__PREFERENCEVARIABLE__ = 'Continue'
                try { $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue' } catch { 'validation rejected write' }
                $PSSettings.ActionPreference.__PREFERENCE__
                $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
                (Get-Variable __PREFERENCEVARIABLE__).Attributes[0].GetType().Name
                $__PREFERENCEVARIABLE__
            }
            & {
                New-Variable __PREFERENCEVARIABLE__ -Value 'SilentlyContinue' -Option Private
                $PSSettings.ActionPreference.__PREFERENCE__
                & { $PSSettings.ActionPreference.__PREFERENCE__ }
                $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
                (Get-Variable __PREFERENCEVARIABLE__).Options
                $PSSettings.ActionPreference.__PREFERENCE__
            }
        }
        $result | Should -Be @(
            'validation rejected write', 'Continue', 'ValidateSetAttribute', 'Stop',
            'SilentlyContinue', 'Stop', 'Private', 'Continue'
        )
    }

    It 'preserves <PreferenceName> value and metadata when an attribute edit is rejected' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            & {
                [ValidateSet('Continue', 'Stop')]$__PREFERENCEVARIABLE__ = 'Continue'
                $retained = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                $retained.Description = 'original'
                $originalAttribute = $retained.Attributes[0]

                try {
                    $retained.Attributes.Add([System.Management.Automation.ValidateSetAttribute]::new('Ignore'))
                } catch { 'attribute rejected' }
                "$($retained.Attributes.Count)/$([object]::ReferenceEquals($originalAttribute, $retained.Attributes[0]))/$($retained.Description)/$($retained.Value)"

                try {
                    $ExecutionContext.SessionState.PSVariable.Set([psvariable]::new('__PREFERENCEVARIABLE__', 'Suspend'))
                } catch { 'replacement rejected' }
                "$($retained.Attributes.Count)/$($retained.Description)/$($retained.Options)/$($PSSettings.ActionPreference.__PREFERENCE__)"

                $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
                "$($retained.Value)/$__PREFERENCEVARIABLE__"
            }
        }
        $result | Should -BeExactly @(
            'attribute rejected', '1/True/original/Continue', 'replacement rejected', '1/original/None/Continue', 'Stop/Stop'
        )
    }

    It 'resets implicit advanced-function preferences and keeps explicit command-level precedence' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-AdvancedPreference {
                [CmdletBinding()]
                param()
                "entry: $($PSSettings.ActionPreference.Error)"
                $PSSettings.Reset('ActionPreference.Error')
                "after reset: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                $null -eq (Get-Variable ErrorActionPreference -Scope Local -ErrorAction Ignore)
                & {
                    Write-Error 'expected' 2>$null
                    'nested command used inherited preference'
                }
                Write-Error 'expected' -ErrorAction Ignore
                'ignored'
                $PSSettings.ActionPreference.Error = 'SilentlyContinue'
                "assigned: $ErrorActionPreference"
                Write-Error 'expected'
                'continued with facade preference'
                try { Write-Error 'expected' -ErrorAction Stop } catch { 'command stopped' }
            }
            $PSSettings.ActionPreference.Error = 'Continue'
            Invoke-AdvancedPreference -ErrorAction Stop
            "caller: $($PSSettings.ActionPreference.Error)"
            $PSSettings.ActionPreference.Error = 'Stop'
            try { Write-Error 'expected' } catch { 'facade stopped command' }
        }
        $result | Should -Be @(
            'entry: Stop', 'after reset: Continue/Continue', 'True', 'nested command used inherited preference', 'ignored',
            'assigned: SilentlyContinue', 'continued with facade preference',
            'command stopped', 'caller: Continue', 'facade stopped command'
        )
    }

    It 'uses the inherited policy for a nested command immediately after either form of reset' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-PreferenceReset {
                [CmdletBinding()]
                param([switch] $Dynamic)

                if ($Dynamic) {
                    $method = 'Reset'
                    $PSSettings.$method('ActionPreference.Error')
                } else {
                    $PSSettings.Reset('ActionPreference.Error')
                }

                Write-Error 'expected'
                "continued with $ErrorActionPreference"
            }

            $PSSettings.ActionPreference.Error = 'SilentlyContinue'
            Invoke-PreferenceReset -ErrorAction Stop
            Invoke-PreferenceReset -Dynamic -ErrorAction Stop
        }
        $result | Should -BeExactly @('continued with SilentlyContinue', 'continued with SilentlyContinue')
    }

    It 'keeps an error preference cached for an existing command and uses new values for later commands' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Error = 'SilentlyContinue'
            'first', 'second' | ForEach-Object {
                if ($_ -eq 'second') {
                    $global:ErrorActionPreference = 'Stop'
                }
                $_
            } | Write-Error -ErrorVariable captured
            "completed: $($captured.Count)"
            try { Write-Error 'expected' } catch { 'new command stopped' }
        }
        $result | Should -BeExactly @('completed: 2', 'new command stopped')
    }

    It 'keeps an advanced invocation error preference separate from an inherited AllScope entry' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-ImplicitPreference {
                [CmdletBinding()]
                param()
                "$ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                $PSSettings.Reset('ActionPreference.Error')
                "$ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
            }
            & {
                New-Variable ErrorActionPreference 'Continue' -Option AllScope
                Invoke-ImplicitPreference -ErrorAction Stop
                "caller: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                & {
                    "child: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)/$((Get-Variable ErrorActionPreference -Scope Local).Options)"
                }
            }
        }
        $result | Should -BeExactly @('Stop/Stop', 'Continue/Continue', 'caller: Continue/Continue', 'child: Continue/Continue/AllScope')
    }

    It 'uses typed entries for <PreferenceName> parameters and retains their validation' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            $block = {
                param([ValidateSet('Stop', 'Continue')]$__PREFERENCEVARIABLE__)
                "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try { $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue' } catch { 'parameter validation rejected write' }
                $PSSettings.Reset('ActionPreference.__PREFERENCE__')
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
            & $block 'sToP'
            $block.Invoke('sToP')
            function Invoke-AdvancedParameter {
                [CmdletBinding()]
                param([ValidateSet('Stop', 'Continue')]$__PREFERENCEVARIABLE__)
                "$($__PREFERENCEVARIABLE__.GetType().Name)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
                try { $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue' } catch { 'advanced parameter validation rejected write' }
                $PSSettings.Reset('ActionPreference.__PREFERENCE__')
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
            Invoke-AdvancedParameter -__PREFERENCEVARIABLE__ 'sToP'
            "caller: $__PREFERENCEVARIABLE__"
        }
        $result | Should -BeExactly @(
            'ActionPreference/Stop/Stop', 'parameter validation rejected write', 'Continue/Continue',
            'ActionPreference/Stop/Stop', 'parameter validation rejected write', 'Continue/Continue',
            'ActionPreference/Stop/Stop', 'advanced parameter validation rejected write', 'Continue/Continue',
            'caller: Continue'
        )
    }

    It 'uses explicit ErrorAction for advanced invocations with a named error preference parameter' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-NamedPreference {
                [CmdletBinding()]
                param([ValidateSet('Continue')][string]$ErrorActionPreference)
                "$($ErrorActionPreference.GetType().Name)/$ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
            }
            $PSSettings.ActionPreference.Error = 'SilentlyContinue'
            Invoke-NamedPreference Continue -ErrorAction Stop
            "caller: $ErrorActionPreference"
            . Invoke-NamedPreference Continue
            "caller: $ErrorActionPreference"
            . Invoke-NamedPreference Continue -ErrorAction Stop
            "caller: $ErrorActionPreference"
        }
        $result | Should -BeExactly @(
            'ActionPreference/Stop/Stop', 'caller: SilentlyContinue',
            'ActionPreference/Continue/Continue', 'caller: SilentlyContinue',
            'ActionPreference/Stop/Stop', 'caller: SilentlyContinue'
        )
    }

    It 'keeps the implicit error preference of a dot-sourced advanced invocation temporary' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-DottedPreference {
                [CmdletBinding()]
                param()
                if ($PSBoundParameters.ContainsKey('ErrorAction')) {
                    "entry: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                    $PSSettings.Reset('ActionPreference.Error')
                    "reset: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                    try { Clear-Variable ErrorActionPreference -Scope Local -Force -ErrorAction Stop } catch { 'clear rejected' }
                    "after clear: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
                    $described = Set-Variable ErrorActionPreference -Description 'temporary' -PassThru
                    "metadata: $($described.Value)/$($described.Description)"
                    $PSSettings.Reset('ActionPreference.Error')
                    $written = Set-Variable ErrorActionPreference 'sToP' -Scope Local -PassThru
                    "cmdlet: $($written.Value.GetType().Name)/$($written.Value)/$($PSSettings.ActionPreference.Error)"
                }
                $ErrorActionPreference = 'SilentlyContinue'
                "assigned: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
            }
            $PSSettings.ActionPreference.Error = 'Continue'
            (Get-Variable ErrorActionPreference -Scope Local).Description = 'caller preference'
            . Invoke-DottedPreference -ErrorAction Stop
            "caller: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)/$((Get-Variable ErrorActionPreference -Scope Local).Description)"
            . Invoke-DottedPreference
            "caller: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
            . {
                param($ErrorActionPreference)
                "parameter: $($ErrorActionPreference.GetType().Name)/$ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
            } 'sToP'
            "caller: $ErrorActionPreference/$($PSSettings.ActionPreference.Error)"
        }
        $result | Should -BeExactly @(
            'entry: Stop/Stop', 'reset: Continue/Continue', 'clear rejected', 'after clear: Continue/Continue',
            'metadata: Continue/temporary', 'cmdlet: ActionPreference/Stop/Stop', 'assigned: SilentlyContinue/SilentlyContinue',
            'caller: Continue/Continue/caller preference', 'assigned: SilentlyContinue/SilentlyContinue', 'caller: SilentlyContinue/SilentlyContinue',
            'parameter: ActionPreference/Stop/Stop', 'caller: Stop/Stop'
        )
    }

    It 'resets <PreferenceName> overrides in optimized functions with and without variable breakpoints' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            function Test-PreferenceVariable {
                $__PREFERENCEVARIABLE__ = 'Stop'
                $PSSettings.ActionPreference.__PREFERENCE__
                $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue'
                $__PREFERENCEVARIABLE__
                $method = 'Reset'
                $PSSettings.$method('ActionPreference.__PREFERENCE__')
                $__PREFERENCEVARIABLE__
            }
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            Test-PreferenceVariable
            $global:preferenceBreakpointHits = 0
            $breakpoint = Set-PSBreakpoint -Variable __PREFERENCEVARIABLE__ -Mode Write -Action {
                $global:preferenceBreakpointHits++
            }
            Test-PreferenceVariable
            Remove-PSBreakpoint $breakpoint
            $global:preferenceBreakpointHits
        }
        $result | Should -Be @(
            'Stop', 'SilentlyContinue', 'Continue', 'Stop', 'SilentlyContinue', 'Continue', '2'
        )
    }

    It 'enumerates the canonical error preference and the other preferences in debugger frames' {
        $result = Invoke-ScopedPropertiesTest {
            function Get-PreferenceFrame {
                [CmdletBinding(SupportsShouldProcess)]
                param()

                $frame = (Get-PSCallStack)[0]
                $value = $frame.GetFrameVariables()['ErrorActionPreference'].Value
                "$($value.GetType().Name)/$value"
                $PSSettings.Reset('ActionPreference.Error')
                $entries = $frame.GetFrameVariables()
                "$($entries.ContainsKey('ErrorActionPreference'))/$ErrorActionPreference"

                $preferences = [ordered]@{
                    DebugPreference = $DebugPreference
                    VerbosePreference = $VerbosePreference
                    WarningPreference = $WarningPreference
                    InformationPreference = $InformationPreference
                    ProgressPreference = $ProgressPreference
                    WhatIfPreference = $WhatIfPreference
                    ConfirmPreference = $ConfirmPreference
                }
                foreach ($name in $preferences.Keys) {
                    $value = $preferences[$name]
                    $frameValue = $entries[$name].Value
                    "$name/$($value.GetType().Name)/$value/$($frameValue.GetType().Name)/$frameValue"
                }
            }

            $PSSettings.ActionPreference.Error = 'Continue'
            $options = @{
                ErrorAction = 'Stop'
                Debug = $true
                Verbose = $true
                WarningAction = 'Stop'
                InformationAction = 'Continue'
                ProgressAction = 'SilentlyContinue'
                WhatIf = $true
                Confirm = $false
            }
            Get-PreferenceFrame @options
            . Get-PreferenceFrame @options
        }
        $expected = @(
            'ActionPreference/Stop', 'False/Continue',
            'DebugPreference/ActionPreference/Continue/ActionPreference/Continue',
            'VerbosePreference/ActionPreference/Continue/ActionPreference/Continue',
            'WarningPreference/ActionPreference/Stop/ActionPreference/Stop',
            'InformationPreference/ActionPreference/Continue/ActionPreference/Continue',
            'ProgressPreference/ActionPreference/SilentlyContinue/ActionPreference/SilentlyContinue',
            'WhatIfPreference/Boolean/True/Boolean/True',
            'ConfirmPreference/ConfirmImpact/None/ConfirmImpact/None'
        )
        $result | Should -BeExactly ($expected + $expected)
    }

    It 'reuses all active scoped preferences across dotted command stages' {
        $result = Invoke-ScopedPropertiesTest {
            function Invoke-NestedScopedPreferences {
                [CmdletBinding()]
                param()

                $observations.Add("nested entry/$WarningPreference/$DebugPreference")
                $PSSettings.Reset('ActionPreference.Warning')
                $PSSettings.Reset('ActionPreference.Debug')
                $observations.Add("nested reset/$WarningPreference/$DebugPreference")
                $WarningPreference = 'Stop'
                $PSSettings.ActionPreference.Debug = 'Ignore'
                $observations.Add("nested write/$WarningPreference/$DebugPreference")
            }

            function Invoke-StagedScopedPreferences {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline)][int] $Item)

                begin {
                    $observations.Add("begin/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference")
                    $PSSettings.Reset('ActionPreference.Warning')
                    $InformationPreference = 'Ignore'
                    $PSSettings.ActionPreference.Debug = 'Stop'
                }

                process {
                    $observations.Add("process/$Item/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference")
                    if ($Item -eq 1) {
                        . Invoke-NestedScopedPreferences -WarningAction SilentlyContinue -Debug
                        $observations.Add("after nested/$WarningPreference/$DebugPreference")
                        $PSSettings.ActionPreference.Warning = 'Ignore'
                        $PSSettings.Reset('ActionPreference.Debug')
                    }
                }

                end {
                    $observations.Add("end/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference")
                    $DebugPreference = 'SilentlyContinue'
                }

                clean {
                    $observations.Add("clean/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference")
                }
            }

            $PSSettings.ActionPreference.Error = 'Continue'
            $PSSettings.ActionPreference.Warning = 'Continue'
            $PSSettings.ActionPreference.Information = 'Continue'
            $PSSettings.ActionPreference.Verbose = 'Continue'
            $PSSettings.ActionPreference.Debug = 'Continue'
            $observations = [System.Collections.Generic.List[string]]::new()
            $overrides = @{
                ErrorAction = 'Stop'
                WarningAction = 'Stop'
                InformationAction = 'Stop'
                Verbose = $false
                Debug = $false
            }
            1, 2 | ForEach-Object {
                $observations.Add("producer/$_/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference")
                $_
            } | . Invoke-StagedScopedPreferences @overrides

            $observations
            "caller/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference"
        }
        $result | Should -BeExactly @(
            'begin/Stop/Stop/Stop/SilentlyContinue/SilentlyContinue',
            'producer/1/Continue/Continue/Continue/Continue/Continue',
            'process/1/Stop/Continue/Ignore/SilentlyContinue/Stop',
            'nested entry/SilentlyContinue/Continue', 'nested reset/Continue/Stop', 'nested write/Stop/Ignore',
            'after nested/Continue/Stop',
            'producer/2/Continue/Continue/Continue/Continue/Continue',
            'process/2/Stop/Ignore/Ignore/SilentlyContinue/Continue',
            'end/Stop/Ignore/Ignore/SilentlyContinue/Continue',
            'clean/Stop/Ignore/Ignore/SilentlyContinue/SilentlyContinue',
            'caller/Continue/Continue/Continue/Continue/Continue'
        )
    }

    It 'resets all three common parameter preferences from a dotted invocation frame' {
        $result = Invoke-ScopedPropertiesTest {
            function Test-DottedShouldProcessPreferences {
                [CmdletBinding(SupportsShouldProcess)]
                param()

                "bound/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
                $PSSettings.Reset('ActionPreference.Progress')
                $PSSettings.Reset('ShouldProcess.Confirm')
                $PSSettings.Reset('ShouldProcess.WhatIf')
                "reset/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
            }

            $PSSettings.ActionPreference.Progress = 'Continue'
            $PSSettings.ShouldProcess.Confirm = 'High'
            $PSSettings.ShouldProcess.WhatIf = $false
            . Test-DottedShouldProcessPreferences -ProgressAction SilentlyContinue -WhatIf:$true -Confirm:$false
            "caller/$($PSSettings.ActionPreference.Progress)/$($PSSettings.ShouldProcess.Confirm)/$($PSSettings.ShouldProcess.WhatIf)"
        }
        $result | Should -BeExactly @(
            'bound/SilentlyContinue/None/True',
            'reset/Continue/High/False',
            'caller/Continue/High/False'
        )
    }

    It 'keeps <PreferenceName> stream behavior under inherited settings and explicit overrides' -TestCases $streamCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $stream = '__PREFERENCE__'
            $write = "Write-$stream"
            $usesSwitch = $stream -in 'Verbose', 'Debug'
            $commonParameter = if ($usesSwitch) { $stream } else { $stream + 'Action' }
            $quiet = if ($stream -eq 'Information') { 'Ignore' } else { 'SilentlyContinue' }

            $PSSettings.ActionPreference.__PREFERENCE__ = $quiet
            @(& { & $write 'suppressed' *>&1 }).Count
            $enable = @{ $commonParameter = if ($usesSwitch) { $true } else { 'Continue' } }
            $record = & $write 'explicit continuation' @enable *>&1
            "$($record.GetType().Name)/$record"

            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            $record = & { & $write 'inherited continuation' *>&1 }
            "$($record.GetType().Name)/$record"
            $disable = @{ $commonParameter = if ($usesSwitch) { $false } else { $quiet } }
            @(& $write 'explicit suppression' @disable *>&1).Count

            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            try {
                & { & $write 'inherited stop' *>&1 | Out-Null }
                'unexpected continuation'
            } catch { $_.Exception.GetType().Name }

            $record = & $write 'explicit override of stop' @enable *>&1
            "$($record.GetType().Name)/$record"

            if (-not $usesSwitch) {
                $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
                $stop = @{ $commonParameter = 'Stop' }
                try {
                    & $write 'explicit stop' @stop *>&1 | Out-Null
                    'unexpected continuation'
                } catch { $_.Exception.GetType().Name }
            }

            $PSSettings.ActionPreference.__PREFERENCE__ = 'Continue'
            $records = 'first', 'second' | ForEach-Object {
                if ($_ -eq 'second') {
                    $PSSettings.AtScope('Global').ActionPreference.__PREFERENCE__ = $quiet
                }
                $_
            } | & $write *>&1
            $records -join ','
            @(& $write 'new command' *>&1).Count
        }
        $recordType = $PreferenceName + 'Record'
        $expected = @(
            '0', "$recordType/explicit continuation", "$recordType/inherited continuation",
            '0', 'ActionPreferenceStopException', "$recordType/explicit override of stop"
        )
        if ($PreferenceName -in 'Warning', 'Information') {
            $expected += 'ActionPreferenceStopException'
        }
        # Debug and Information cache their inherited preference for an existing command.
        $expected += if ($PreferenceName -in 'Debug', 'Information') { 'first,second' } else { 'first' }
        $expected += '0'
        $result | Should -BeExactly $expected
    }

    It 'suppresses Information host display while allowing redirected records under SilentlyContinue' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Information = 'SilentlyContinue'
            Write-Information 'suppressed host message'
            $record = Write-Information 'redirected message' 6>&1
            "$($record.GetType().Name)/$($record.MessageData)"
            $PSSettings.ActionPreference.Information = 'Continue'
            & { Write-Information 'inherited host message' }
            Write-Information 'ignored host message' -InformationAction Ignore
        }
        $result | Should -BeExactly @('InformationRecord/redirected message', 'inherited host message')
    }

    It 'preserves stream prompting and Debug and Verbose interactions in the host' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Warning = 'SilentlyContinue'
            $PSSettings.ActionPreference.Verbose = 'SilentlyContinue'
            $PSSettings.ActionPreference.Debug = 'Inquire'
            $PSSettings.ActionPreference.Information = 'Inquire'

            $record = Write-Warning 'verbose enables warning' -Verbose 3>&1
            "$($record.GetType().Name)/$record"
            $record = Write-Debug 'explicit debug continues' -Debug 5>&1
            "$($record.GetType().Name)/$record"

            try { Write-Debug 'debug prompts' 5>$null } catch { "Debug/$($_.FullyQualifiedErrorId)" }
            try { Write-Verbose 'debug enables verbose prompting' -Debug 4>$null } catch { "Verbose/$($_.FullyQualifiedErrorId)" }
            try { Write-Warning 'debug enables warning prompting' -Debug 3>$null } catch { "Warning/$($_.FullyQualifiedErrorId)" }
            try { Write-Information 'information prompts' 6>$null } catch { "Information/$($_.FullyQualifiedErrorId)" }
            $PSSettings.ActionPreference.Warning = 'Inquire'
            try { Write-Warning 'inherited warning prompts' 3>$null } catch { "Warning/$($_.FullyQualifiedErrorId)" }
            $PSSettings.ActionPreference.Verbose = 'Inquire'
            try { Write-Verbose 'inherited verbose prompts' 4>$null } catch { "Verbose/$($_.FullyQualifiedErrorId)" }

            @(Write-Warning 'explicit warning action wins' -Debug -WarningAction SilentlyContinue 3>&1).Count
            @(Write-Verbose 'explicit false wins' -Debug -Verbose:$false 4>&1).Count
            @(Write-Debug 'explicit false wins' -Debug:$false 5>&1).Count

            $hostless = [powershell]::Create()
            try {
                $null = $hostless.AddScript({
                    $PSSettings.ActionPreference.Debug = 'Inquire'
                    $record = Write-Debug 'hostless debug' 5>&1
                    "$($record.GetType().Name)/$record"
                    $record = Write-Verbose 'hostless verbose' -Debug 4>&1
                    "$($record.GetType().Name)/$record"
                })
                $hostless.Invoke()
                $hostless.HadErrors
            } finally {
                $hostless.Dispose()
            }
        }
        $result | Should -BeExactly @(
            'WarningRecord/verbose enables warning', 'DebugRecord/explicit debug continues',
            'Debug/InvalidOperation,Microsoft.PowerShell.Commands.WriteDebugCommand',
            'Verbose/InvalidOperation,Microsoft.PowerShell.Commands.WriteVerboseCommand',
            'Warning/InvalidOperation,Microsoft.PowerShell.Commands.WriteWarningCommand',
            'Information/InvalidOperation,Microsoft.PowerShell.Commands.WriteInformationCommand',
            'Warning/InvalidOperation,Microsoft.PowerShell.Commands.WriteWarningCommand',
            'Verbose/InvalidOperation,Microsoft.PowerShell.Commands.WriteVerboseCommand',
            '0', '0', '0', 'DebugRecord/hostless debug', 'VerboseRecord/hostless verbose', 'False'
        )
    }

    It 'binds <PreferenceName> views to script, local, and global scopes and expires local views' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            $scriptPath = Join-Path ([IO.Path]::GetTempPath()) ("ScopedProperties-{0}.ps1" -f [guid]::NewGuid())
            try {
                @'
$script:__PREFERENCEVARIABLE__ = 'sToP'
$scriptSettings = $PSSettings.AtScope('Script')
& {
    $localSettings = $PSSettings.AtScope('Local')
    $local:__PREFERENCEVARIABLE__ = 'silentlycontinue'
    $global:__PREFERENCEVARIABLE__ = 'continue'
    "$($localSettings.ActionPreference.__PREFERENCE__)/$($scriptSettings.ActionPreference.__PREFERENCE__)/$($PSSettings.AtScope('Global').ActionPreference.__PREFERENCE__)"
    $scriptSettings.Reset('ActionPreference.__PREFERENCE__')
    $localSettings.Reset('ActionPreference.__PREFERENCE__')
    $PSSettings.ActionPreference.__PREFERENCE__
}
'@ | Set-Content -LiteralPath $scriptPath
                & $scriptPath
            } finally {
                Remove-Item -LiteralPath $scriptPath -Force
            }
            $expiredView = & { $PSSettings.AtScope('Local') }
            try { $expiredView.ActionPreference.__PREFERENCE__ } catch { 'expired read rejected' }
            try { $expiredView.ActionPreference.__PREFERENCE__ = 'Stop' } catch { 'expired assignment rejected' }
            try { $expiredView.Reset('ActionPreference.__PREFERENCE__') } catch { 'expired reset rejected' }
            $expiredGroup = & { $PSSettings.AtScope('Local').ActionPreference }
            try { $expiredGroup.__PREFERENCE__ } catch { 'expired group read rejected' }
            try { $expiredGroup.__PREFERENCE__ = 'Stop' } catch { 'expired group assignment rejected' }
            try { $PSSettings.AtScope('Private') } catch { 'unsupported scope rejected' }
            try { $PSSettings.Reset('Style.Progress.Unknown') } catch { 'unknown path rejected' }
        }
        $result | Should -Be @(
            'SilentlyContinue/Stop/Continue', 'Continue', 'expired read rejected',
            'expired assignment rejected', 'expired reset rejected', 'expired group read rejected',
            'expired group assignment rejected', 'unsupported scope rejected', 'unknown path rejected'
        )
    }

    It 'resets progress overrides in a bound scope and expires captured bound groups' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.Style.Progress.View = 'Minimal'
            $expiredProgress = & {
                $scopeView = $PSSettings.AtScope('Local')
                $scopeView.Style.Progress.View = 'Classic'
                & {
                    $PSStyle.Progress.View = 'Classic'
                    $scopeView.Reset('Style.Progress.View')
                    Write-Host "$($scopeView.Style.Progress.View)/$($PSStyle.Progress.View)"
                }
                Write-Host $PSStyle.Progress.View
                $scopeView.Style.Progress
            }
            try { $expiredProgress.View } catch { 'expired progress read rejected' }
        }
        $result | Should -Be @('Minimal/Classic', 'Minimal', 'expired progress read rejected')
    }

    It 'keeps captured <PreferenceName> variables synchronized until their last binding is reset' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            & {
                $__PREFERENCEVARIABLE__ = 'Continue'
                $captured = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue'
                "$($captured.Value.GetType().Name)/$($captured.Value)"
                $captured.Value = 'cOnTiNuE'
                "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"

                $PSSettings.Reset('ActionPreference.__PREFERENCE__')
                "$($null -eq $captured.Value)/$__PREFERENCEVARIABLE__"
                $captured.Value = 'detached'
                "$($captured.Value)/$($null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Local -ErrorAction Ignore))/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }
        }
        $result | Should -BeExactly @('ActionPreference/SilentlyContinue', 'Continue/Continue', 'True/Stop', 'detached/True/Stop')
    }

    It 'retains captured <PreferenceName> variable values after scope exit' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            $captured = & {
                $__PREFERENCEVARIABLE__ = 'SilentlyContinue'
                Get-Variable __PREFERENCEVARIABLE__ -Scope Local
            }

            "$($captured.Value.GetType().Name)/$($captured.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"
            $captured.Value = 'cOnTiNuE'
            "$($captured.Value)/$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -BeExactly @('ActionPreference/SilentlyContinue/Stop', 'Continue/Stop/Stop')
    }

    It 'resolves configured settings in module scopes rather than importing callers' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Error = 'Continue'
            $PSSettings.Style.Progress.View = 'Minimal'
            $progress = $PSSettings.Style.Progress
            $module = New-Module {
                $PSSettings.ActionPreference.Error = 'Stop'
                $PSStyle.Progress.View = 'Classic'
                function Get-ModuleSetting($capturedProgress) {
                    "$($PSSettings.ActionPreference.Error)/$($capturedProgress.View)"
                    $PSSettings.ActionPreference.Error = 'SilentlyContinue'
                    "$ErrorActionPreference/$($PSSettings.AtScope('Script').ActionPreference.Error)"
                }
            }
            & {
                $PSSettings.ActionPreference.Error = 'SilentlyContinue'
                & $module { param($capturedProgress) Get-ModuleSetting $capturedProgress } $progress
            }
            "$($PSSettings.ActionPreference.Error)/$($PSStyle.Progress.View)"
        }
        $result | Should -Be @('Stop/Classic', 'SilentlyContinue/Stop', 'Continue/Minimal')
    }

    It 'isolates progress settings across runspaces and rejects foreign bound views' {
        $result = Invoke-ScopedPropertiesTest {
            $PSSettings.ActionPreference.Error = 'Stop'
            $PSStyle.Progress.View = 'Classic'
            $runspacePowerShell = [powershell]::Create()
            try {
                $null = $runspacePowerShell.AddScript({
                    param($foreignView, $unboundProgress)
                    "$($PSSettings.ActionPreference.Error)/$($unboundProgress.View)"
                    try { $foreignView.Style.Progress.View } catch { 'foreign view rejected' }
                    $PSStyle.Progress.View = 'Classic'
                    $PSSettings.Reset('Style.Progress.View')
                    $PSStyle.Progress.View
                }).AddArgument($PSSettings.AtScope('Global')).AddArgument($PSSettings.Style.Progress)
                $runspacePowerShell.Invoke()
                $runspacePowerShell.HadErrors
            } finally {
                $runspacePowerShell.Dispose()
            }
            $PSStyle.Progress.View
        }
        $result | Should -Be @('Continue/Minimal', 'foreign view rejected', 'Minimal', 'False', 'Classic')
    }

    It 'copies ordinary and foreign <PreferenceName> variable objects into the target setting' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
            & {
                $supplied = [psvariable]::new('__PREFERENCEVARIABLE__', 'SilentlyContinue')
                $supplied.Description = 'supplied'
                $ExecutionContext.SessionState.PSVariable.Set($supplied)
                $stored = Get-Variable __PREFERENCEVARIABLE__ -Scope Local
                "$([object]::ReferenceEquals($supplied, $stored))/$($stored.Value.GetType().Name)/$($stored.Description)"
                $supplied.Value = 'Stop'
                "$($supplied.Value)/$($stored.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"
            }

            $source = Get-Variable __PREFERENCEVARIABLE__ -Scope Global
            $source.Description = 'source runspace'
            $target = [powershell]::Create()
            try {
                $null = $target.AddScript({
                    param($foreign, $foreignView, $unboundPreferences)

                    try { $foreignView.__PREFERENCE__ } catch { 'foreign view rejected' }
                    $unboundPreferences.__PREFERENCE__
                    & {
                        $variables = $ExecutionContext.SessionState.PSVariable
                        $variables.Set($foreign)
                        $imported = $variables.Get('__PREFERENCEVARIABLE__')
                        "$([object]::ReferenceEquals($foreign, $imported))/$($imported.Value)/$($imported.Description)"
                        $PSSettings.ActionPreference.__PREFERENCE__ = 'SilentlyContinue'
                        $imported.Description = 'destination runspace'
                        "$($imported.Value)/$($foreign.Value)/$($foreign.Description)"
                    }
                }).AddArgument($source).AddArgument($PSSettings.AtScope('Global').ActionPreference).AddArgument($PSSettings.ActionPreference)
                $target.Invoke()
                $target.HadErrors
            } finally {
                $target.Dispose()
            }

            "$($source.Value)/$($source.Description)/$($PSSettings.ActionPreference.__PREFERENCE__)"
        }
        $result | Should -BeExactly @(
            'False/ActionPreference/supplied', 'Stop/SilentlyContinue/SilentlyContinue',
            'foreign view rejected', $DefaultPreference, 'False/Stop/source runspace', 'SilentlyContinue/Stop/source runspace', 'False', 'Stop/source runspace/Stop'
        )
    }

    It 'shares exported module <PreferenceName> preferences within the same runspace' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $module = New-Module -Name ScopedPreferenceExport -Verbose:$false {
                $__PREFERENCEVARIABLE__ = 'Stop'
                function Set-ScopedExportedPreference {
                    $script:__PREFERENCEVARIABLE__ = 'SilentlyContinue'
                }
                function Get-ScopedExportedPreference {
                    "$($PSSettings.ActionPreference.__PREFERENCE__)/$((Get-Variable __PREFERENCEVARIABLE__ -Scope Script).Description)"
                }
                Export-ModuleMember -Variable __PREFERENCEVARIABLE__ -Function Set-ScopedExportedPreference, Get-ScopedExportedPreference -Verbose:$false
            }
            Import-Module $module -Verbose:$false
            $exported = $module.ExportedVariables['__PREFERENCEVARIABLE__']
            $imported = Get-Variable __PREFERENCEVARIABLE__ -Scope Global
            [object]::ReferenceEquals($exported, $imported)
            Set-ScopedExportedPreference
            $imported.Description = 'same runspace'
            "$($imported.Value)/$($PSSettings.ActionPreference.__PREFERENCE__)"
            Get-ScopedExportedPreference

            $foreignShell = [powershell]::Create()
            try {
                $null = $foreignShell.AddScript({
                    param($foreignModule)
                    Import-Module $foreignModule -Verbose:$false
                    $foreignExport = $foreignModule.ExportedVariables['__PREFERENCEVARIABLE__']
                    $foreignImport = Get-Variable __PREFERENCEVARIABLE__ -Scope Global
                    [object]::ReferenceEquals($foreignExport, $foreignImport)
                    $PSSettings.ActionPreference.__PREFERENCE__ = 'Stop'
                    $foreignImport.Description = 'other runspace'
                    "$($foreignImport.Value)/$($foreignExport.Value)/$($foreignExport.Description)"
                    Remove-Module $foreignModule.Name -Verbose:$false
                    "$($null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Global -ErrorAction Ignore))/$($PSSettings.ActionPreference.__PREFERENCE__)"
                }).AddArgument($module)
                $foreignShell.Invoke()
                $foreignShell.Streams.Error.Count
            } finally {
                $foreignShell.Dispose()
            }

            Remove-Module $module.Name -Verbose:$false
            "$($null -eq (Get-Variable __PREFERENCEVARIABLE__ -Scope Global -ErrorAction Ignore))/$($PSSettings.ActionPreference.__PREFERENCE__)/$($exported.Value)"
        }
        $result | Should -BeExactly @(
            'True', 'SilentlyContinue/SilentlyContinue', 'SilentlyContinue/same runspace',
            'False', 'Stop/SilentlyContinue/same runspace', "True/$DefaultPreference", '0',
            "True/$DefaultPreference/SilentlyContinue"
        )
    }

    It 'normalizes <PreferenceName> host initialization and session-state API writes in the same entry' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName, $DefaultPreference)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()
            $iss.Variables.Add([System.Management.Automation.Runspaces.SessionStateVariableEntry]::new(
                '__PREFERENCEVARIABLE__', 'sIlEnTlYcOnTiNuE', 'Host preference'))
            $runspace = [runspacefactory]::CreateRunspace($iss)
            $controller = [powershell]::Create()
            try {
                $runspace.Open()
                $controller.Runspace = $runspace
                $proxy = $runspace.SessionStateProxy
                $value = $proxy.GetVariable('__PREFERENCEVARIABLE__')
                "$($value.GetType().Name)/$value"
                $proxy.SetVariable('__PREFERENCEVARIABLE__', 'sToP')
                try { $proxy.SetVariable('__PREFERENCEVARIABLE__', $null) } catch { 'host write rejected' }

                $null = $controller.AddScript({
                    $variables = $ExecutionContext.SessionState.PSVariable
                    "$__PREFERENCEVARIABLE__/$($PSSettings.ActionPreference.__PREFERENCE__)/$($variables.Get('__PREFERENCEVARIABLE__').Description)"
                    $variables.Set([psvariable]::new('__PREFERENCEVARIABLE__', 'cOnTiNuE'))
                    $value = $variables.GetValue('__PREFERENCEVARIABLE__')
                    "$($value.GetType().Name)/$value/$($PSSettings.ActionPreference.__PREFERENCE__)"
                    $variables.Remove('__PREFERENCEVARIABLE__')
                    "$($null -eq $variables.Get('__PREFERENCEVARIABLE__'))/$($PSSettings.ActionPreference.__PREFERENCE__)"
                })
                $controller.Invoke()
                $controller.HadErrors
            } finally {
                $controller.Dispose()
                $runspace.Dispose()
            }
        }
        $result | Should -BeExactly @(
            'ActionPreference/SilentlyContinue', 'host write rejected', 'Stop/Stop/Host preference',
            'ActionPreference/Continue/Continue', "True/$DefaultPreference", 'False'
        )
    }

    It 'rejects invalid <PreferenceName> host initialization and retains the preceding entry' -TestCases $preferenceCases {
        param($PreferenceName, $VariableName)

        $result = Invoke-ScopedPropertiesTest -PreferenceName $PreferenceName -VariableName $VariableName {
            $iss = [initialsessionstate]::CreateDefault2()
            $iss.Variables.Add([System.Management.Automation.Runspaces.SessionStateVariableEntry]::new(
                '__PREFERENCEVARIABLE__', 'sToP', 'valid host entry'))
            $iss.Variables.Add([System.Management.Automation.Runspaces.SessionStateVariableEntry]::new(
                '__PREFERENCEVARIABLE__', 'invalid host preference', 'invalid host entry'))

            $relaxed = [runspacefactory]::CreateRunspace($iss)
            try {
                $relaxed.Open()
                $variable = $relaxed.SessionStateProxy.PSVariable.Get('__PREFERENCEVARIABLE__')
                "$($variable.Value.GetType().Name)/$($variable.Value)/$($variable.Description)"
            } finally {
                $relaxed.Dispose()
            }

            $iss.ThrowOnRunspaceOpenError = $true
            $strict = [runspacefactory]::CreateRunspace($iss)
            try {
                try { $strict.Open() } catch { $_.Exception.GetBaseException().Message.Contains('invalid host preference') }
                $strict.RunspaceStateInfo.State
            } finally {
                $strict.Dispose()
            }
        }
        $result | Should -BeExactly @('ActionPreference/Stop/valid host entry', 'True', 'Broken')
    }

    It 'rejects <Expression> and directs users to AtScope' -TestCases @(
        @{ Expression = '$global:PSSettings' }
        @{ Expression = '${script:PSSettings}' }
        @{ Expression = '$local:PSSettings.Style.Progress.View' }
        @{ Expression = '"$private:PSSettings"' }
    ) {
        param($Expression)

        $template = @'
            try {
                & ([scriptblock]::Create('__EXPRESSION__'))
            } catch {
                $_.Exception.Message.Contains('AtScope')
            }
'@
        $script = [scriptblock]::Create($template.Replace('__EXPRESSION__', $Expression))
        Invoke-ScopedPropertiesTest $script | Should -Be 'True'
    }

    It 'enforces constrained language restrictions on facade methods and setters' {
        $result = Invoke-ScopedPropertiesTest {
            $runspace = [runspacefactory]::CreateRunspace()
            $runspace.Open()
            $runspace.SessionStateProxy.LanguageMode = 'ConstrainedLanguage'
            $runspacePowerShell = [powershell]::Create()
            try {
                $runspacePowerShell.Runspace = $runspace
                $null = $runspacePowerShell.AddScript(@'
try { $PSSettings.AtScope('Local') } catch { $_.FullyQualifiedErrorId }
try { $PSSettings.Style.Progress.View = 'Classic' } catch { $_.FullyQualifiedErrorId }
'@)
                $runspacePowerShell.Invoke()
            } finally {
                $runspacePowerShell.Dispose()
                $runspace.Dispose()
            }
        }
        $result | Should -Be @(
            'MethodInvocationNotSupportedInConstrainedLanguage',
            'PropertySetterNotSupportedInConstrainedLanguage'
        )
    }

    It 'shares typed Warning and Information values with legacy variables and captured adapters' {
        $result = Invoke-ScopedPropertiesTest {
            foreach ($name in 'Warning', 'Information') {
                & {
                    $variableName = "${name}Preference"
                    $view = $PSSettings.AtScope('Local')
                    "$name/$($view.ActionPreference.$name)"
                    $view.ActionPreference.$name = 'Stop'
                    $variable = Get-Variable $variableName -Scope Local
                    "$($variable.Value.GetType().Name)/$($variable.Value)"
                    Set-Variable $variableName 'iGnOrE' -Scope Local
                    $view.ActionPreference.$name
                    $variable.Value = 'Continue'

                    foreach ($invalid in 'Suspend', 'invalid preference', 500, $null) {
                        try { $variable.Value = $invalid } catch { "rejected/$($view.ActionPreference.$name)" }
                    }

                    $view.Reset("actionpreference.$($name.ToUpperInvariant())")
                    "$($null -eq $variable.Value)/$($view.ActionPreference.$name)"
                }
            }
        }
        $result | Should -BeExactly @(
            'Warning/Continue', 'ActionPreference/Stop', 'Ignore',
            'rejected/Continue', 'rejected/Continue', 'rejected/Continue', 'rejected/Continue', 'True/Continue',
            'Information/SilentlyContinue', 'ActionPreference/Stop', 'Ignore',
            'rejected/Continue', 'rejected/Continue', 'rejected/Continue', 'rejected/Continue', 'True/SilentlyContinue'
        )
    }

    It 'keeps dotted Error, Warning, and Information overrides independent across command stages' {
        $result = Invoke-ScopedPropertiesTest {
            function Test-NamedPreferences {
                param($WarningPreference, $InformationPreference)
                "$($WarningPreference.GetType().Name)/$($PSSettings.ActionPreference.Warning)/$($InformationPreference.GetType().Name)/$($PSSettings.ActionPreference.Information)"
            }

            function Test-DottedPreferences {
                [CmdletBinding(SupportsShouldProcess)]
                param(
                    [ValidateSet('SilentlyContinue')][string] $InformationPreference,
                    [ValidateSet('Continue')][string] $WarningPreference
                )
                begin {
                    "begin/$ErrorActionPreference/$WarningPreference/$InformationPreference/$ProgressPreference/$WhatIfPreference/$ConfirmPreference"
                    $frame = (Get-PSCallStack)[0]
                    $entries = $frame.GetFrameVariables()
                    "frame/$($entries.ErrorActionPreference.Value)/$($entries.WarningPreference.Value)/$($entries.InformationPreference.Value)"

                    $PSSettings.Reset('actionpreference.WARNING')
                    "reset/$($frame.GetFrameVariables().ContainsKey('WarningPreference'))/$($PSSettings.ActionPreference.Warning)"

                    $variables = @{}
                    Get-Variable -Scope Local | ForEach-Object { $variables[$_.Name] = $_ }
                    "variables/$($variables.ErrorActionPreference.Value)/$($variables.WarningPreference.Value)/$($variables.InformationPreference.Value)"
                    $WarningPreference = 'Ignore'
                }
                process { "process/$($PSSettings.ActionPreference.Error)/$($PSSettings.ActionPreference.Warning)/$($PSSettings.ActionPreference.Information)" }
                end { "end/$ErrorActionPreference/$WarningPreference/$InformationPreference" }
                clean { $global:scopedWarningInformationCleanup = "clean/$ErrorActionPreference/$WarningPreference/$InformationPreference" }
            }

            $PSSettings.ActionPreference.Error = 'Continue'
            $PSSettings.ActionPreference.Warning = 'SilentlyContinue'
            $PSSettings.ActionPreference.Information = 'SilentlyContinue'
            Test-NamedPreferences 'sToP' 'iGnOrE'
            . Test-DottedPreferences SilentlyContinue Continue -ErrorAction Stop -WarningAction Stop -InformationAction Continue -ProgressAction SilentlyContinue -WhatIf -Confirm:$false
            $global:scopedWarningInformationCleanup
            "caller/$ErrorActionPreference/$WarningPreference/$InformationPreference"
        }
        $result | Should -BeExactly @(
            'ActionPreference/Stop/ActionPreference/Ignore',
            'begin/Stop/Stop/Continue/SilentlyContinue/True/None',
            'frame/Stop/Stop/Continue', 'reset/False/SilentlyContinue', 'variables/Stop/SilentlyContinue/Continue',
            'process/Stop/Ignore/Continue', 'end/Stop/Ignore/Continue', 'clean/Stop/Ignore/Continue',
            'caller/Continue/SilentlyContinue/SilentlyContinue'
        )
    }

    It 'uses inherited Warning and Information preferences for stream output and explicit overrides' {
        $result = Invoke-ScopedPropertiesTest {
            function Write-ScopedRecords {
                [CmdletBinding()]
                param()
                Write-Warning 'warning record'
                Write-Information 'information record'
            }

            $PSSettings.ActionPreference.Warning = 'Ignore'
            $PSSettings.ActionPreference.Information = 'Ignore'
            "ignored/$(@(Write-ScopedRecords 3>&1 6>&1).Count)"
            Write-ScopedRecords -WarningAction Continue -InformationAction Continue 3>&1 6>&1 |
                ForEach-Object { "override/$($_.GetType().Name)/$_" }

            $PSSettings.ActionPreference.Warning = 'Continue'
            $PSSettings.ActionPreference.Information = 'Continue'
            Write-ScopedRecords 3>&1 6>&1 | ForEach-Object { "inherited/$($_.GetType().Name)/$_" }

            $PSSettings.ActionPreference.Warning = 'Stop'
            try { & { Write-Warning 'stopping warning' 3>$null } } catch { 'warning stopped' }
            $PSSettings.ActionPreference.Information = 'Stop'
            try { & { Write-Information 'stopping information' 6>$null } } catch { 'information stopped' }
        }
        $result | Should -BeExactly @(
            'ignored/0', 'override/WarningRecord/warning record', 'override/InformationRecord/information record',
            'inherited/WarningRecord/warning record', 'inherited/InformationRecord/information record',
            'warning stopped', 'information stopped'
        )
    }

    It 'initializes Verbose and Debug only when their switches are explicit' {
        $result = Invoke-ScopedPropertiesTest {
            function Get-TracePreferences {
                [CmdletBinding()]
                param()
                $entries = (Get-PSCallStack)[0].GetFrameVariables()
                "$VerbosePreference/$DebugPreference/$($PSSettings.ActionPreference.Verbose)/$($PSSettings.ActionPreference.Debug)/$($entries.ContainsKey('VerbosePreference'))/$($entries.ContainsKey('DebugPreference'))"
            }

            $PSSettings.ActionPreference.Verbose = 'Ignore'
            $PSSettings.ActionPreference.Debug = 'Stop'
            Get-TracePreferences
            Get-TracePreferences -Verbose
            Get-TracePreferences -Debug
            Get-TracePreferences -Verbose:$false -Debug:$false
            "caller/$VerbosePreference/$DebugPreference"
        }
        $result | Should -BeExactly @(
            'Ignore/Stop/Ignore/Stop/False/False', 'Continue/Stop/Continue/Stop/True/False',
            'Ignore/Continue/Ignore/Continue/False/True',
            'SilentlyContinue/SilentlyContinue/SilentlyContinue/SilentlyContinue/True/True', 'caller/Ignore/Stop'
        )
    }

    It 'enumerates all five preferences through nested dotted invocations' {
        $result = Invoke-ScopedPropertiesTest {
            function Test-InnerPreferences {
                [CmdletBinding()]
                param(
                    [ValidateSet('Stop')][string] $VerbosePreference,
                    [ValidateSet('Stop')][string] $DebugPreference
                )

                $frame = (Get-PSCallStack)[0]
                $entries = $frame.GetFrameVariables()
                "frame/$($entries.ErrorActionPreference.Value)/$($entries.WarningPreference.Value)/$($entries.InformationPreference.Value)/$($entries.VerbosePreference.Value)/$($entries.DebugPreference.Value)"
                "tuple/$ProgressPreference/$WhatIfPreference/$ConfirmPreference"

                $PSSettings.Reset('ActionPreference.Debug')
                "reset/$($frame.GetFrameVariables()['DebugPreference'].Value)/$($PSSettings.ActionPreference.Debug)/$($PSSettings.ActionPreference.Verbose)"
                $DebugPreference = 'Ignore'
                $VerbosePreference = 'Ignore'

                $variables = @{}
                Get-Variable -Scope Local | ForEach-Object { $variables[$_.Name] = $_ }
                "variables/$($variables.ErrorActionPreference.Value)/$($variables.WarningPreference.Value)/$($variables.InformationPreference.Value)/$($variables.VerbosePreference.Value)/$($variables.DebugPreference.Value)"
            }

            function Test-OuterPreferences {
                [CmdletBinding(SupportsShouldProcess)]
                param()

                . Test-InnerPreferences -VerbosePreference Stop -DebugPreference Stop -Verbose:$false -Debug:$false -WarningAction Stop
                "outer/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference"
            }

            $PSSettings.ActionPreference.Error = 'Continue'
            $PSSettings.ActionPreference.Warning = 'SilentlyContinue'
            $PSSettings.ActionPreference.Information = 'SilentlyContinue'
            $PSSettings.ActionPreference.Verbose = 'SilentlyContinue'
            $PSSettings.ActionPreference.Debug = 'SilentlyContinue'
            . Test-OuterPreferences -ErrorAction Stop -WarningAction Continue -InformationAction Continue -Verbose -Debug -ProgressAction SilentlyContinue -WhatIf -Confirm:$false
            "caller/$ErrorActionPreference/$WarningPreference/$InformationPreference/$VerbosePreference/$DebugPreference"
        }
        $result | Should -BeExactly @(
            'frame/Stop/Stop/Continue/SilentlyContinue/SilentlyContinue', 'tuple/SilentlyContinue/True/None',
            'reset/Continue/Continue/SilentlyContinue', 'variables/Stop/Stop/Continue/Ignore/Ignore',
            'outer/Stop/Continue/Continue/Continue/Continue', 'caller/Continue/SilentlyContinue/SilentlyContinue/SilentlyContinue/SilentlyContinue'
        )
    }

    It 'uses inherited Verbose and Debug preferences and explicit switches for stream output' {
        $result = Invoke-ScopedPropertiesTest {
            function Write-ScopedTraceRecords {
                [CmdletBinding()]
                param()
                Write-Verbose 'verbose record'
                Write-Debug 'debug record'
            }

            $PSSettings.ActionPreference.Verbose = 'SilentlyContinue'
            $PSSettings.ActionPreference.Debug = 'SilentlyContinue'
            "suppressed/$(@(Write-ScopedTraceRecords 4>&1 5>&1).Count)"
            Write-ScopedTraceRecords -Verbose -Debug 4>&1 5>&1 | ForEach-Object { "explicit/$($_.GetType().Name)/$_" }

            $PSSettings.ActionPreference.Verbose = 'Continue'
            $PSSettings.ActionPreference.Debug = 'Continue'
            Write-ScopedTraceRecords 4>&1 5>&1 | ForEach-Object { "inherited/$($_.GetType().Name)/$_" }
            "explicit false/$(@(Write-ScopedTraceRecords -Verbose:$false -Debug:$false 4>&1 5>&1).Count)"

            $PSSettings.ActionPreference.Verbose = 'Stop'
            try { & { Write-Verbose 'stopping verbose' 4>$null } } catch { 'verbose stopped' }
            $PSSettings.ActionPreference.Debug = 'Stop'
            try { & { Write-Debug 'stopping debug' 5>$null } } catch { 'debug stopped' }
        }
        $result | Should -BeExactly @(
            'suppressed/0', 'explicit/VerboseRecord/verbose record', 'explicit/DebugRecord/debug record',
            'inherited/VerboseRecord/verbose record', 'inherited/DebugRecord/debug record', 'explicit false/0',
            'verbose stopped', 'debug stopped'
        )
    }
}
