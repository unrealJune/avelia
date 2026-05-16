#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test "$repoRoot/tests/Avelia.E2E/Avelia.E2E.fsproj" `
    --logger "console;verbosity=minimal"
