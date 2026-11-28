# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The test verifies that the facade reflects the global PROFILE binding.')]
[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidAssignmentToAutomaticVariable', '', Justification = 'The test deliberately changes and restores PWD to verify live facade identity.')]
param()

class RuntimeThisProbe {
    [bool] SameThis([object] $facade) { return [object]::ReferenceEquals($this, $facade.Invocation.This) }
}

Describe 'PSScopedProperties runtime views' -Tag 'CI' {
    $skipFeatureTests = -not [ExperimentalFeature]::IsEnabled('PSScopedProperties')
    It 'keeps runtime containers stable, non-consuming, and exposes actual automatic values' -Skip:$skipFeatureTests {
        function Get-RuntimeIdentity {
            param([Parameter(ValueFromPipeline)] $Item)
            process {
                [pscustomobject]@{
                    InfoSame = [object]::ReferenceEquals($MyInvocation, $PSSettings.Invocation.Info)
                    InputSame = [object]::ReferenceEquals($input, $PSSettings.Invocation.Input)
                    ExecutionContextSame = [object]::ReferenceEquals($ExecutionContext, $PSSettings.Session.ExecutionContext)
                    HostSame = [object]::ReferenceEquals($Host, $PSSettings.Session.Host)
                    InputType = $PSSettings.Invocation.Input.GetType().FullName
                    InputStillReadable = $Item
                    Display = "$($PSSettings.Invocation) $($PSSettings.Session)"
                }
            }
        }

        $observed = @('a', 'b' | Get-RuntimeIdentity)
        $observed.Count | Should -Be 2
        $observed[0].InfoSame | Should -BeTrue
        $observed[0].InputSame | Should -BeTrue
        $observed[0].ExecutionContextSame | Should -BeTrue
        $observed[0].HostSame | Should -BeTrue
        $observed[0].InputStillReadable | Should -Be 'a'
        $observed[0].Display | Should -Not -BeNullOrEmpty
    }

    It 'resolves invocation variables from active function and dotted script frames' -Skip:$skipFeatureTests {
        function Get-InvocationView {
            [CmdletBinding()]
            param($First, $Second)
            [pscustomobject]@{
                Args = @($PSSettings.Invocation.Arguments)
                Bound = $PSSettings.Invocation.BoundParameters
                InfoSame = [object]::ReferenceEquals($PSSettings.Invocation.Info, $MyInvocation)
                CmdletSame = [object]::ReferenceEquals($PSSettings.Invocation.Cmdlet, $PSCmdlet)
                FunctionName = $PSSettings.Invocation.Info.MyCommand.Name
                ScriptRoot = $PSSettings.Invocation.ScriptRoot
                CommandPath = $PSSettings.Invocation.CommandPath
            }
        }

        $view = Get-InvocationView -First one -Second two
        $view.Args | Should -BeNullOrEmpty
        $view.Bound.First | Should -Be 'one'
        $view.Bound.Second | Should -Be 'two'
        $view.InfoSame | Should -BeTrue
        $view.CmdletSame | Should -BeTrue
        $view.FunctionName | Should -Be 'Get-InvocationView'

        function Get-ArgsView {
            [pscustomobject]@{
                Legacy = $args
                Facade = $PSSettings.Invocation.Arguments
                Same = [object]::ReferenceEquals($args, $PSSettings.Invocation.Arguments)
            }
        }
        $arguments = Get-ArgsView alpha beta
        $arguments.Legacy | Should -Be @('alpha', 'beta')
        $arguments.Facade | Should -Be @('alpha', 'beta')
        $arguments.Same | Should -BeTrue

        $dotScript = Join-Path $TestDrive 'runtime-dotted.ps1'
        $scriptText = '[pscustomobject]@{ Root=$PSSettings.Invocation.ScriptRoot; Path=$PSSettings.Invocation.CommandPath; InfoRoot=$PSSettings.Invocation.Info.PSScriptRoot; InfoPath=$PSSettings.Invocation.Info.PSCommandPath; LegacyInfoRoot=$MyInvocation.PSScriptRoot; LegacyInfoPath=$MyInvocation.PSCommandPath }'
        Set-Content -LiteralPath $dotScript -Value $scriptText
        $dot = . $dotScript
        $dot.Root | Should -Be (Split-Path -Parent $dotScript)
        $dot.Path | Should -Be $dotScript
        $dot.InfoRoot | Should -Be $dot.LegacyInfoRoot
        $dot.InfoPath | Should -Be $dot.LegacyInfoPath
    }

    It 'routes runtime reads through selected scopes and preserves legacy setters' -Skip:$skipFeatureTests {
        $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction Ignore
        $savedExitCodeValue = if ($null -ne $savedExitCode) { $savedExitCode.Value }
        try {
            $global:LASTEXITCODE = 31
            $PSSettings.AtScope('Global').LastExitCode | Should -Be 31
            $PSSettings.LastExitCode = 32
            $LASTEXITCODE | Should -Be 32
            $global:LASTEXITCODE | Should -Be 31
            $PSSettings.AtScope('Global').LastExitCode = 33
            $global:LASTEXITCODE | Should -Be 33
            $PSSettings.LastExitCode | Should -Be 32
            { $PSSettings.Reset('Runtime.ProcessId') } | Should -Throw
            { $PSSettings.Reset('Invocation.Arguments') } | Should -Throw
        } finally {
            if ($null -ne $savedExitCode) {
                Set-Variable LASTEXITCODE $savedExitCodeValue -Scope Global
            } else {
                Remove-Variable LASTEXITCODE -Scope Global -Force -ErrorAction Ignore
            }
        }
    }

    It 'distinguishes absent optional runtime values from explicit null under strict modes' -Skip:$skipFeatureTests {
        $savedProfile = Get-Variable PROFILE -Scope Global -ErrorAction Ignore
        $savedProfileValue = if ($null -ne $savedProfile) { $savedProfile.Value }
        try {
            if ($null -ne $savedProfile) {
                $PSSettings.Session.Profile | Should -Be $savedProfileValue
            }
            Remove-Variable PROFILE -Scope Global -Force -ErrorAction Ignore
            Set-StrictMode -Off
            $PSSettings.Session.Profile | Should -BeNullOrEmpty
            "$($PSSettings.Session.Profile)" | Should -BeExactly ''

            Set-StrictMode -Version 1
            { $PSSettings.Session.Profile } | Should -Throw
            "$($PSSettings.Session.Profile)" | Should -BeExactly ''

            Set-StrictMode -Version 2
            { $PSSettings.Session.Profile } | Should -Throw
            { "$($PSSettings.Session.Profile)" } | Should -Throw

            $global:PROFILE = $null
            $PSSettings.Session.Profile | Should -BeNullOrEmpty
            "$($PSSettings.Session.Profile)" | Should -BeExactly ''
        } finally {
            Set-StrictMode -Off
            Remove-Variable PROFILE -Scope Global -Force -ErrorAction Ignore
            if ($null -ne $savedProfile) { Set-Variable PROFILE $savedProfileValue -Scope Global }
        }
    }

    It 'uses PWD aliases and runspace location ownership, including caller shadowing' -Skip:$skipFeatureTests {
        $originalLocation = Get-Location
        Push-Location $originalLocation
        try {
            Set-Location $TestDrive
            $PSSettings.Session.CurrentLocation.Path | Should -Be (Get-Location).Path
            function Read-ShadowedPwd {
                $PWD = 'shadow-value'
                $PSSettings.Session.CurrentLocation | Should -BeExactly 'shadow-value'
                $PWD = Get-Location
                [object]::ReferenceEquals($PWD, $PSSettings.Session.CurrentLocation) | Should -BeTrue
            }
            Read-ShadowedPwd
        } finally {
            Pop-Location
        }
        $PSSettings.Session.CurrentLocation.Path | Should -Be $originalLocation.Path
    }

    It 'preserves class and ETS this ownership in methods, script properties, and script methods' -Skip:$skipFeatureTests {
        $instance = [RuntimeThisProbe]::new()
        $instance.SameThis($PSSettings) | Should -BeTrue

        Add-Member -InputObject $instance -MemberType ScriptProperty -Name ScopedThis -Value {
            [object]::ReferenceEquals($this, $PSSettings.Invocation.This)
        } -Force
        Add-Member -InputObject $instance -MemberType ScriptMethod -Name ScopedThisMethod -Value {
            [object]::ReferenceEquals($this, $PSSettings.Invocation.This)
        } -Force
        $instance.ScopedThis | Should -BeTrue
        $instance.ScopedThisMethod() | Should -BeTrue
    }

    It 'keeps input identity and does not consume input during access or display' -Skip:$skipFeatureTests {
        function Test-InputView {
            $facadeInput = $PSSettings.Invocation.Input
            $display = "$($PSSettings) $($PSSettings.Invocation)"
            [object]::ReferenceEquals($facadeInput, $input) | Should -BeTrue
            $display | Should -Not -BeNullOrEmpty
            $seen = [System.Collections.Generic.List[object]]::new()
            while ($facadeInput.MoveNext()) {
                $seen.Add($facadeInput.Current)
            }
            $seen | Should -Be @('record-1', 'record-2')
        }
        'record-1', 'record-2' | Test-InputView
    }

    It 'preserves foreach and switch enumerator identity and restores nested loop state' -Skip:$skipFeatureTests {
        $seen = [System.Collections.Generic.List[object]]::new()
        foreach ($outer in 1, 2) {
            $outerEnumerator = $PSSettings.Invocation.ForEachEnumerator
            [object]::ReferenceEquals($outerEnumerator, $foreach) | Should -BeTrue
            foreach ($inner in 'x') {
                [object]::ReferenceEquals($PSSettings.Invocation.ForEachEnumerator, $foreach) | Should -BeTrue
                $seen.Add("$outer$inner")
            }
            [object]::ReferenceEquals($PSSettings.Invocation.ForEachEnumerator, $outerEnumerator) | Should -BeTrue
            try { foreach ($inner in 'throw') { throw 'unwind inner loop' } } catch { $_.Exception.Message | Should -Be 'unwind inner loop' }
            [object]::ReferenceEquals($PSSettings.Invocation.ForEachEnumerator, $outerEnumerator) | Should -BeTrue
            foreach ($inner in 'break') { break }
            [object]::ReferenceEquals($PSSettings.Invocation.ForEachEnumerator, $outerEnumerator) | Should -BeTrue
        }
        $seen | Should -Be @('1x', '2x')

        switch -Regex ('alpha') {
            'alpha' {
                [object]::ReferenceEquals($PSSettings.Invocation.SwitchEnumerator, $switch) | Should -BeTrue
                $PSSettings.Invocation.SwitchEnumerator.Current | Should -Be 'alpha'
                $outerSwitch = $PSSettings.Invocation.SwitchEnumerator
                try { switch ('inner') { default { throw 'unwind switch' } } } catch { $_.Exception.Message | Should -Be 'unwind switch' }
                [object]::ReferenceEquals($PSSettings.Invocation.SwitchEnumerator, $outerSwitch) | Should -BeTrue
                switch ('inner') { default { break } }
                [object]::ReferenceEquals($PSSettings.Invocation.SwitchEnumerator, $outerSwitch) | Should -BeTrue
            }
        }

        foreach ($x in 1, 2) {
            try { if ($x -eq 1) { throw 'early' } } catch { $_.Exception.Message | Should -Be 'early' }
        }
        $PSSettings.Invocation.ForEachEnumerator | Should -BeNullOrEmpty
        $PSSettings.Invocation.SwitchEnumerator | Should -BeNullOrEmpty
    }

    It 'keeps Matches from the last successful scalar, collection, and switch-regex match' -Skip:$skipFeatureTests {
        'abc' -match '(?<letters>a)' | Should -BeTrue
        [object]::ReferenceEquals($Matches, $PSSettings.Invocation.Matches) | Should -BeTrue
        $PSSettings.Invocation.Matches.letters | Should -Be 'a'
        'no-match' -match 'never' | Should -BeFalse
        $PSSettings.Invocation.Matches.letters | Should -Be 'a'

        @('first-1', 'second-2') -match '(?<digit>\d)' | Out-Null
        $PSSettings.Invocation.Matches.letters | Should -Be 'a'
        switch -Regex ('switch-3') { '(?<digit>\d)' { $PSSettings.Invocation.Matches.digit | Should -Be '3' } }
    }

    It 'returns live diagnostic references and preserves error history identity' -Skip:$skipFeatureTests {
        $errors = $PSSettings.Diagnostics.Errors
        [object]::ReferenceEquals($errors, $Error) | Should -BeTrue
        $count = $errors.Count
        Write-Error 'runtime-view-error' -ErrorAction Continue
        $PSSettings.Diagnostics.Errors.Count | Should -BeGreaterThan $count
        [object]::ReferenceEquals($errors, $PSSettings.Diagnostics.Errors) | Should -BeTrue
        $PSSettings.Diagnostics.StackTrace | Should -Be $StackTrace
        $PSSettings.Diagnostics.ToString() | Should -Not -BeNullOrEmpty
    }

    It 'keeps event values and event-local Error lists for a subscription lifetime' -Skip:$skipFeatureTests {
        $eventName = 'ScopedRuntimeEvent_' + [guid]::NewGuid().ToString('N')
        $secondEventName = $eventName + '_second'
        $received = [System.Collections.Generic.List[object]]::new()
        $secondReceived = [System.Collections.Generic.List[object]]::new()
        function Wait-RuntimeCallback($observations, $count, $job) {
            $deadline = [datetime]::UtcNow.AddSeconds(5)
            while ($observations.Count -lt $count -and [datetime]::UtcNow -lt $deadline) {
                Wait-Event -Timeout 1 | Out-Null
            }
            $observations.Count | Should -Be $count -Because ($job.Error | Out-String)
        }
        $action = {
            $Event.MessageData.Observations.Add([pscustomobject]@{
                RecordSame = [object]::ReferenceEquals($Event, $PSSettings.Event.Record)
                Record = $PSSettings.Event.Record
                SubscriberSame = [object]::ReferenceEquals($EventSubscriber, $PSSettings.Event.Subscriber)
                ArgsSame = [object]::ReferenceEquals($EventArgs, $PSSettings.Event.Arguments)
                SenderSame = [object]::ReferenceEquals($Sender, $PSSettings.Event.Sender)
                Errors = $PSSettings.Diagnostics.Errors
                ErrorCount = $PSSettings.Diagnostics.Errors.Count
            })
            Write-Error 'event-local-error' -ErrorAction Continue
        }
        $secondAction = {
            $Event.MessageData.Observations.Add($PSSettings.Diagnostics.Errors)
        }
        try {
            $firstJob = Register-EngineEvent -SourceIdentifier $eventName -Action $action
            $secondJob = Register-EngineEvent -SourceIdentifier $secondEventName -Action $secondAction
            New-Event -SourceIdentifier $eventName -MessageData @{ Observations = $received; Marker = 'one' } | Out-Null
            Wait-RuntimeCallback $received 1 $firstJob
            New-Event -SourceIdentifier $eventName -MessageData @{ Observations = $received; Marker = 'two' } | Out-Null
            Wait-RuntimeCallback $received 2 $firstJob
            New-Event -SourceIdentifier $secondEventName -MessageData @{ Observations = $secondReceived } | Out-Null
            Wait-RuntimeCallback $secondReceived 1 $secondJob
            $received.Count | Should -Be 2
            $received[0].RecordSame | Should -BeTrue
            $received[0].SubscriberSame | Should -BeTrue
            $received[0].ArgsSame | Should -BeTrue
            $received[0].SenderSame | Should -BeTrue
            [object]::ReferenceEquals($received[0].Errors, $received[1].Errors) | Should -BeTrue
            $received[1].ErrorCount | Should -BeGreaterThan $received[0].ErrorCount
            [object]::ReferenceEquals($received[0].Errors, $secondReceived[0]) | Should -BeFalse
            $persistedEvent = & $firstJob.Module { $PSSettings.Event.Record }
            [object]::ReferenceEquals($persistedEvent, $received[1].Record) | Should -BeTrue
            [object]::ReferenceEquals($received[0].Errors, $Error) | Should -BeFalse
        } finally {
            Unregister-Event -SourceIdentifier $eventName -ErrorAction Ignore
            Unregister-Event -SourceIdentifier $secondEventName -ErrorAction Ignore
            Get-Event -SourceIdentifier $eventName -ErrorAction Ignore | Remove-Event -ErrorAction Ignore
            Get-Event -SourceIdentifier $secondEventName -ErrorAction Ignore | Remove-Event -ErrorAction Ignore
            $firstJob, $secondJob | Remove-Job -Force -ErrorAction Ignore
        }
    }

    It 'keeps optional producer values absent outside debugger, profile, prompt, and remoting lifetimes' -Skip:$skipFeatureTests {
        Set-StrictMode -Off
        $PSSettings.Diagnostics.DebugContext | Should -BeNullOrEmpty
        $legacyProfile = Get-Variable PROFILE -ErrorAction Ignore
        if ($null -eq $legacyProfile) {
            $PSSettings.Session.Profile | Should -BeNullOrEmpty
        } else {
            $PSSettings.Session.Profile | Should -Be $legacyProfile.Value
        }
        $PSSettings.Session.CurrentlyExecutingCommand | Should -BeNullOrEmpty
        $PSSettings.Remoting.SenderInfo | Should -BeNullOrEmpty
        { $PSSettings.Invocation.Cmdlet } | Should -Not -Throw
        { $PSSettings.Invocation.ScriptRoot } | Should -Not -Throw
        { $PSSettings.Invocation.CommandPath } | Should -Not -Throw
    }

    It 'keeps process, runspace, culture, platform, and language constant values live' -Skip:$skipFeatureTests {
        $PSSettings.Runtime.ProcessId | Should -Be $PID
        $PSSettings.Runtime.InstallationDirectory | Should -Be $PSHOME
        $PSSettings.Runtime.Edition | Should -Be $PSEdition
        $PSSettings.Runtime.ShellId | Should -Be $ShellId
        [object]::ReferenceEquals($PSSettings.Runtime.VersionTable, $PSVersionTable) | Should -BeTrue
        $PSSettings.Runtime.Culture | Should -Be $PSCulture
        $PSSettings.Runtime.UICulture | Should -Be $PSUICulture
        $PSSettings.Runtime.IsWindows | Should -Be ([bool]$IsWindows)
        $PSSettings.Runtime.IsLinux | Should -Be ([bool]$IsLinux)
        $PSSettings.Runtime.IsMacOS | Should -Be ([bool]$IsMacOS)
        $PSSettings.Runtime.IsCoreCLR | Should -Be ([bool]$IsCoreCLR)
        $PSSettings.Constants.True | Should -Be $true
        $PSSettings.Constants.False | Should -Be $false
        $PSSettings.Constants.Null | Should -BeNullOrEmpty
        $PSSettings.LastStatementSuccessful | Should -Be $?
        { $PSSettings.LastStatementSuccessful = $false } | Should -Throw
    }

    It 'tracks parser history tokens and success separately from exit status' -Skip:$skipFeatureTests {
        $tokenComparison = & {
            [pscustomobject]@{ LegacyFirst = $^; FacadeFirst = $PSSettings.History.FirstToken; LegacyLast = $$; FacadeLast = $PSSettings.History.LastToken }
        }
        $tokenComparison.FacadeFirst | Should -Be $tokenComparison.LegacyFirst
        $tokenComparison.FacadeLast | Should -Be $tokenComparison.LegacyLast
        $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction Ignore
        $savedExitCodeValue = if ($null -ne $savedExitCode) { $savedExitCode.Value }
        try {
            $global:LASTEXITCODE = 7
            $PSSettings.LastExitCode | Should -Be 7
            $null = 'success'
            $success = $PSSettings.LastStatementSuccessful
            $success | Should -BeTrue
            Write-Error 'success-flag-failure' -ErrorAction SilentlyContinue
            $PSSettings.LastStatementSuccessful | Should -BeFalse
            $PSSettings.LastExitCode | Should -Be 7
        } finally {
            if ($null -ne $savedExitCode) {
                Set-Variable LASTEXITCODE $savedExitCodeValue -Scope Global
            } else {
                Remove-Variable LASTEXITCODE -Scope Global -Force -ErrorAction Ignore
            }
        }
    }

    It 'preserves runspace ownership and rejects foreign and expired bound views' -Skip:$skipFeatureTests {
        $captured = $PSSettings.AtScope('Local').Session
        $runspace = [runspacefactory]::CreateRunspace()
        try {
            $runspace.Open()
            $powershell = [powershell]::Create()
            try {
                $powershell.Runspace = $runspace
                $runspace.SessionStateProxy.SetVariable('captured', $captured)
                $powershell.AddScript({ try { $captured.Host } catch { $_.Exception.Message } }) | Out-Null
                $foreign = $powershell.Invoke()
                $foreign | Should -Match 'different runspace'
            } finally { $powershell.Dispose() }
        } finally { $runspace.Dispose() }

        $expired = & { $PSSettings.AtScope('Local').Session }
        { $expired.Host } | Should -Throw
        $PSSettings.Session.Host | Should -Not -BeNullOrEmpty
    }
}
