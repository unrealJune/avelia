#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test "$repoRoot/Avelia.sln" `
    -c $Configuration `
    --no-build `
    --filter "Category!=Integration&Category!=E2E&Category!=Performance" `
    --logger "console;verbosity=minimal"
