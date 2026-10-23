# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Delegates to Invoke-SelectStringMeasurement.
.DESCRIPTION
Reloads the performance module and derives parameters and completion from it.
Run ./test/perf/SelectString/Initialize-Performance.ps1 before requesting full
help in a fresh session, or request help on the module command after importing.
.FORWARDHELPTARGETNAME Invoke-SelectStringMeasurement
.FORWARDHELPCATEGORY Function
#>
[CmdletBinding()]
param()

dynamicparam {
    . (Join-Path $PSScriptRoot 'Initialize-Performance.ps1')
    SelectString.Performance\New-SelectStringScriptParameters -CommandName Invoke-SelectStringMeasurement
}

process {
    SelectString.Performance\Invoke-SelectStringMeasurement @PSBoundParameters
}
