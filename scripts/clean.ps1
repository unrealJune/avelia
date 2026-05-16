#Requires -Version 7.0
<#
.SYNOPSIS
    Nuke all build output: bin/, obj/, packages/, TestResults/, AppPackages/, GeneratedArtifacts/.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$patterns = @("bin", "obj", "packages", "TestResults", "AppPackages", "BundleArtifacts", "GeneratedArtifacts")

foreach ($pattern in $patterns) {
    Get-ChildItem -Path $repoRoot -Recurse -Force -Directory -Filter $pattern -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\\.git\\' } |
        ForEach-Object {
            Write-Host "  removing $($_.FullName)" -ForegroundColor DarkGray
            Remove-Item -Recurse -Force -LiteralPath $_.FullName
        }
}

Write-Host "Clean complete." -ForegroundColor Green
