#Requires -Version 7.0
<#
.SYNOPSIS
Increments the Teams/Copilot app patch version and creates a new upload ZIP.
.EXAMPLE
.\Update-TeamsPackage.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'tools/New-TeamsPackage.ps1') -IncrementVersion
