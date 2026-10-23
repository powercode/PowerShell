# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Delegates to Invoke-SelectStringComparison.
.DESCRIPTION
Reloads the performance module and derives parameters and completion from it.
Run ./test/perf/SelectString/Initialize-Performance.ps1 before requesting full
help in a fresh session, or request help on the module command after importing.
.FORWARDHELPTARGETNAME Invoke-SelectStringComparison
.FORWARDHELPCATEGORY Function
#>
[CmdletBinding()]
param()

dynamicparam {
    . (Join-Path $PSScriptRoot '../perf/SelectString/Initialize-Performance.ps1')
    SelectString.Performance\New-SelectStringScriptParameters -CommandName Invoke-SelectStringComparison
}

process {
    SelectString.Performance\Invoke-SelectStringComparison @PSBoundParameters
}
