# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
@{
    RootModule = 'SelectString.Performance.psm1'
    ModuleVersion = '0.1.0'
    GUID = 'ea5b67d8-2ba8-4549-9631-482f41c8d7ed'
    Author = 'PowerShell Contributors'
    Copyright = '(c) Microsoft Corporation. Licensed under the MIT License.'
    Description = 'Select-String benchmark fixtures, isolated measurements and ETW capture.'
    PowerShellVersion = '7.4'
    FunctionsToExport = @(
        'Invoke-SelectStringMeasurement'
        'Invoke-SelectStringCapture'
        'New-SelectStringLogFixture'
        'New-SelectStringScriptParameters'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
}
