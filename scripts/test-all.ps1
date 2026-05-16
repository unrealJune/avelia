#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test "$repoRoot/Avelia.sln" --logger "console;verbosity=minimal"
