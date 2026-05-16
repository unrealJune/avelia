#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test "$repoRoot/Avelia.sln" `
    --no-build `
    --filter "Category!=Integration&Category!=E2E&Category!=Performance" `
    --logger "console;verbosity=minimal"
