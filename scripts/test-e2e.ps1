#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test "$repoRoot/tests/Avelia.E2E/Avelia.E2E.fsproj" `
    -c $Configuration `
    --logger "console;verbosity=minimal"
