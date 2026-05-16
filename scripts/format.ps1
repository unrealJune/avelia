#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Check
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if ($Check) {
        dotnet tool run fantomas --check src/ tests/
        if ($LASTEXITCODE -ne 0) { throw "fantomas check failed" }
        dotnet csharpier check .
        if ($LASTEXITCODE -ne 0) { throw "csharpier check failed" }
    } else {
        dotnet tool run fantomas src/ tests/
        if ($LASTEXITCODE -ne 0) { throw "fantomas failed" }
        dotnet csharpier format .
        if ($LASTEXITCODE -ne 0) { throw "csharpier failed" }
    }
} finally {
    Pop-Location
}
