# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Loads or reloads the Select-String performance commands for an interactive session.
.DESCRIPTION
Force-imports the module manifest into global scope so command help is available
before invoking an entry script. The entry scripts also call this initializer
when binding parameters, so ordinary execution needs no separate setup.
.EXAMPLE
./test/perf/SelectString/Initialize-Performance.ps1
Get-Help Invoke-SelectStringCapture -Full
#>
[CmdletBinding()]
param()

Import-Module (Join-Path $PSScriptRoot 'SelectString.Performance.psd1') -Force -Global
