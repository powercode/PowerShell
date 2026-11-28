# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

Describe 'PSScopedProperties mutable configuration' -Tag 'CI' {
    $skipFeatureTests = -not [ExperimentalFeature]::IsEnabled('PSScopedProperties')
    BeforeAll {
        function Get-ScopedMutableDefaultProbe {
            [CmdletBinding()]
            param([int] $Value = 5)
            $Value
        }

        function Invoke-ScopedMutableDefaultWithParameter {
            [CmdletBinding()]
            param([System.Collections.IDictionary] $PSDefaultParameterValues)
            Get-ScopedMutableDefaultProbe
        }

        function Invoke-ScopedMutableSnapshotProbe {
            [CmdletBinding()]
            param(
                [System.Collections.IDictionary] $PSDefaultParameterValues,
                [int] $Value = 5
            )
            $Value
        }
    }

    It 'keeps captured dictionary views caller-dynamic and isolates child mutations' -Skip:$skipFeatureTests {
        $outerKey = 'ScopedMutableOuter:Value'
        $childKey = 'ScopedMutableChild:Value'
        $PSDefaultParameterValues.Remove($outerKey)
        $PSDefaultParameterValues.Remove($childKey)
        $PSDefaultParameterValues[$outerKey] = 31
        $captured = $PSSettings.Command.DefaultParameterValues
        $legacyCaptured = $PSDefaultParameterValues

        & {
            $captured[$childKey] = 42
            $legacyCaptured['ScopedMutableLegacyChild:Value'] = 43
            $PSSettings.Command.DefaultParameterValues[$childKey] | Should -Be 42
            $PSSettings.Command.DefaultParameterValues[$outerKey] | Should -Be 31
        }

        $PSDefaultParameterValues.ContainsKey($childKey) | Should -BeFalse
        $PSDefaultParameterValues.ContainsKey('ScopedMutableLegacyChild:Value') | Should -BeFalse
        $PSDefaultParameterValues[$outerKey] | Should -Be 31
        $PSDefaultParameterValues.Remove($outerKey)
    }

    It 'does not bind a child override when a dictionary mutation fails' -Skip:$skipFeatureTests {
        $duplicateKey = 'ScopedDuplicate:Value'
        $PSDefaultParameterValues.Remove($duplicateKey)
        $PSDefaultParameterValues[$duplicateKey] = 1
        & {
            $local = $PSSettings.AtScope('Local')
            $before = Get-Variable -Name PSDefaultParameterValues -Scope Local -ErrorAction Ignore
            { $local.Command.DefaultParameterValues.Add('not-a-valid-default-key', 1) } | Should -Throw
            $afterInvalid = Get-Variable -Name PSDefaultParameterValues -Scope Local -ErrorAction Ignore
            $afterInvalid | Should -Be $before

            { $local.Command.DefaultParameterValues.Add($duplicateKey, 2) } | Should -Throw
            $afterDuplicate = Get-Variable -Name PSDefaultParameterValues -Scope Local -ErrorAction Ignore
            $afterDuplicate | Should -Be $before
        }
        $PSDefaultParameterValues.Remove($duplicateKey)
    }

    It 'preserves explicit null and makes previously captured views unavailable' -Skip:$skipFeatureTests {
        $saved = $PSSettings.Command.DefaultParameterValues.Clone()
        $captured = $PSSettings.Command.DefaultParameterValues
        try {
            $PSSettings.Command.DefaultParameterValues = $null
            $PSDefaultParameterValues | Should -BeNullOrEmpty
            $PSSettings.Command.DefaultParameterValues | Should -BeNullOrEmpty
            { $captured.Count } | Should -Throw
            { $captured.Add('ScopedNull:Value', 1) } | Should -Throw

            $PSDefaultParameterValues = $saved
            ($null -ne $PSSettings.Command.DefaultParameterValues) | Should -BeTrue
            $PSSettings.Command.DefaultParameterValues.Count | Should -Be $saved.Count
            $PSSettings.Command.DefaultParameterValues = $null
            $PSDefaultParameterValues | Should -BeNullOrEmpty
            $PSSettings.Command.DefaultParameterValues | Should -BeNullOrEmpty
        }
        finally {
            $PSSettings.Command.DefaultParameterValues = $saved
        }
    }

    It 'routes IDictionary and IEnumerable reads and mutations through the scoped backing' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableEnumerable:Value'
        $PSDefaultParameterValues.Remove($key)

        & {
            $values = $PSSettings.Command.DefaultParameterValues
            ([System.Collections.IDictionary]$values).Add($key, 9)
            ([System.Collections.IDictionary]$values).Contains($key) | Should -BeTrue
            $values.Count | Should -BeGreaterThan 0
            $enumerator = ([System.Collections.IEnumerable]$values).GetEnumerator()
            $enumerated = [System.Collections.Generic.List[object]]::new()
            while ($enumerator.MoveNext()) { $enumerated.Add($enumerator.Current) }
            $enumerated.Count | Should -BeGreaterThan 0
            @($enumerated | Where-Object { $_.Key -eq $key }).Count | Should -Be 1
        }

        $PSDefaultParameterValues.ContainsKey($key) | Should -BeFalse
    }

    It 'applies scriptblock defaults through the pipeline caller snapshot' -Skip:$skipFeatureTests {
        $key = 'Get-ScopedMutableDefaultProbe:Value'
        $originalValue = $PSDefaultParameterValues[$key]
        try {
            $PSSettings.Command.DefaultParameterValues[$key] = { 73 }
            Get-ScopedMutableDefaultProbe | Should -Be 73

            & {
                $PSSettings.Command.DefaultParameterValues[$key] = { 84 }
                Get-ScopedMutableDefaultProbe | Should -Be 84
            }

            Get-ScopedMutableDefaultProbe | Should -Be 73

            Invoke-ScopedMutableDefaultWithParameter -PSDefaultParameterValues @{ 'Get-ScopedMutableDefaultProbe:Value' = { 84 } } | Should -Be 84
            Get-ScopedMutableDefaultProbe | Should -Be 73

            $snapshotKey = 'Invoke-ScopedMutableSnapshotProbe:Value'
            $PSSettings.Command.DefaultParameterValues[$snapshotKey] = 73
            Invoke-ScopedMutableSnapshotProbe -PSDefaultParameterValues @{ 'Invoke-ScopedMutableSnapshotProbe:Value' = 84 } | Should -Be 73
        }
        finally {
            if ($null -eq $originalValue) {
                $PSDefaultParameterValues.Remove($key)
            }
            else {
                $PSSettings.Command.DefaultParameterValues[$key] = $originalValue
            }
        }
    }

    It 'keeps ordinary references stored inside dictionary entries shared across copy-on-write' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableNested:Value'
        $nested = [pscustomobject]@{ Value = 1 }
        $PSDefaultParameterValues.Remove($key)
        $PSSettings.Command.DefaultParameterValues[$key] = $nested

        & {
            $PSSettings.Command.DefaultParameterValues[$key].Value = 2
            $PSSettings.Command.DefaultParameterValues['ScopedMutableNestedChild:Value'] = 3
        }

        $PSDefaultParameterValues[$key].Value | Should -Be 2
        $PSDefaultParameterValues.ContainsKey('ScopedMutableNestedChild:Value') | Should -BeFalse
        $PSDefaultParameterValues.Remove($key)
    }

    It 'returns an ordinary retained value from a PSVariable after its scope exits' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableRetained:Value'
        $PSDefaultParameterValues.Remove($key)
        $retainedVariable = & {
            $PSDefaultParameterValues = $PSDefaultParameterValues.Clone()
            $PSDefaultParameterValues[$key] = 17
            Get-Variable -Name PSDefaultParameterValues -Scope Local
        }

        $retainedVariable.Value.GetType().FullName | Should -BeLike '*DefaultParameterDictionary'
        $retainedVariable.Value[$key] | Should -Be 17
        $retainedVariable.Value.Remove($key)
        $PSDefaultParameterValues.ContainsKey($key) | Should -BeFalse
    }

    It 'keeps a PSVariable object attached to its exact binding while a child scope is active' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableExactBinding:Value'
        $global:PSDefaultParameterValues[$key] = 11
        $retainedVariable = Get-Variable -Name PSDefaultParameterValues -Scope Global

        & {
            $PSDefaultParameterValues = @{ $key = 22 }
            $retainedVariable.Value[$key] | Should -Be 11
            (Get-Variable -Name PSDefaultParameterValues -Scope Global -ValueOnly)[$key] | Should -Be 11
        }

        $global:PSDefaultParameterValues.Remove($key)
    }

    It 'clears a retained PSVariable object when its binding is explicitly removed' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableRemoved:Value'
        $retainedVariable = & {
            $PSDefaultParameterValues = @{ $key = 17 }
            $variable = Get-Variable -Name PSDefaultParameterValues -Scope Local
            Remove-Variable -Name PSDefaultParameterValues -Scope Local -Force
            $variable
        }

        $retainedVariable.Value | Should -BeNullOrEmpty
    }

    It 'anchors explicitly qualified mutable variable reads and writes to their named scopes' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableQualified:Value'
        $scriptDefaults = Get-Variable PSDefaultParameterValues -Scope Script -ErrorAction Ignore
        $originalScriptDefaults = if ($scriptDefaults) { $scriptDefaults.Value }
        $script:PSDefaultParameterValues = $PSDefaultParameterValues.Clone()
        try {
            $global:PSDefaultParameterValues[$key] = 31
            $script:PSDefaultParameterValues[$key] = 32

            & {
                $PSDefaultParameterValues = @{ $key = 33 }

                $global:PSDefaultParameterValues[$key] | Should -Be 31
                $script:PSDefaultParameterValues[$key] | Should -Be 32
                $local:PSDefaultParameterValues[$key] | Should -Be 33

                $global:PSDefaultParameterValues[$key] = 41
                $script:PSDefaultParameterValues[$key] = 42
                $local:PSDefaultParameterValues[$key] = 43

                $global:PSDefaultParameterValues[$key] | Should -Be 41
                $script:PSDefaultParameterValues[$key] | Should -Be 42
                $local:PSDefaultParameterValues[$key] | Should -Be 43
            }

            $global:PSDefaultParameterValues[$key] | Should -Be 41
            $script:PSDefaultParameterValues[$key] | Should -Be 42
        } finally {
            $global:PSDefaultParameterValues.Remove($key)
            if ($scriptDefaults) {
                $script:PSDefaultParameterValues = $originalScriptDefaults
            } else {
                Remove-Variable PSDefaultParameterValues -Scope Script
            }
        }
    }

    It 'allows mutable member changes on read-only bindings but rejects replacement' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableReadOnly:Value'
        & {
            $local = $PSSettings.AtScope('Local')
            $PSDefaultParameterValues = @{ $key = 51 }
            Set-Variable -Name PSDefaultParameterValues -Option ReadOnly -Scope Local

            $PSDefaultParameterValues[$key] = 52
            $PSDefaultParameterValues[$key] | Should -Be 52
            { $local.Command.DefaultParameterValues = @{} } | Should -Throw
            $PSDefaultParameterValues[$key] | Should -Be 52
        }

        $PSDefaultParameterValues.ContainsKey($key) | Should -BeFalse
    }

    It 'allows mutable member changes on constant bindings but rejects replacement' -Skip:$skipFeatureTests {
        $key = 'ScopedMutableConstant:Value'
        & {
            $local = $PSSettings.AtScope('Local')
            New-Variable -Name PSDefaultParameterValues -Scope Local -Value @{ $key = 61 } -Option Constant

            $PSDefaultParameterValues[$key] = 62
            $PSDefaultParameterValues[$key] | Should -Be 62
            { $local.Command.DefaultParameterValues = @{} } | Should -Throw
            $PSDefaultParameterValues[$key] | Should -Be 62
        }

        $PSDefaultParameterValues.ContainsKey($key) | Should -BeFalse
    }
}
