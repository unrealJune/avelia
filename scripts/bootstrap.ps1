#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot dev environment setup for Avelia.
.DESCRIPTION
    Installs every prerequisite, restores packages, builds the solution, and runs
    the fast test tier as a smoke test. Designed to take a fresh Windows 11 box to
    a working dev environment in under 15 minutes.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Step([string]$msg, [scriptblock]$action) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Step failed: $msg (exit $LASTEXITCODE)"
    }
    Write-Host "    ok" -ForegroundColor Green
}

function Has-Command($name) {
    [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

Step "Check PowerShell 7+" {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        Write-Host "PowerShell 5 detected. Installing PowerShell 7 via winget..."
        winget install --id Microsoft.PowerShell -e --silent --accept-package-agreements --accept-source-agreements
        Write-Host ""
        Write-Host "PowerShell 7 installed. Open a new pwsh terminal and re-run this script." -ForegroundColor Yellow
        exit 0
    }
    Write-Host "    pwsh $($PSVersionTable.PSVersion)"
}

Step "Install .NET SDK" {
    $required = (Get-Content "$repoRoot/global.json" -Raw | ConvertFrom-Json).sdk.version
    $installed = (dotnet --list-sdks) -join "`n"
    if ($installed -match [regex]::Escape($required)) {
        Write-Host "    .NET SDK $required already installed"
        return
    }
    Write-Host "    Installing .NET SDK $required via winget..."
    $major = ($required -split '\.')[0]
    winget install --id "Microsoft.DotNet.SDK.$major" -e --silent --accept-package-agreements --accept-source-agreements
}

Step "Install winapp CLI" {
    if (Has-Command winapp) {
        Write-Host "    winapp already present"
        return
    }
    winget install Microsoft.winappcli --source winget --silent --accept-package-agreements --accept-source-agreements
    if (-not (Has-Command winapp)) {
        throw "winapp install reported success but the CLI is not on PATH. Open a new terminal and re-run."
    }
}

Step "Install WinUI dotnet new templates" {
    $existing = (dotnet new list 2>&1 | Out-String)
    if ($existing -match 'Microsoft\.WindowsAppSDK\.WinUI\.CSharp\.Templates') {
        Write-Host "    templates already installed"
        return
    }
    dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
}

Step "Initialize Windows App SDK for shell project" {
    $shell = "$repoRoot/src/Avelia.Shell.Windows"
    if (-not (Test-Path $shell)) {
        throw "Shell project missing at $shell — did you clone correctly?"
    }
    Push-Location $shell
    try {
        winapp init --use-defaults
    } finally {
        Pop-Location
    }
}

Step "Generate developer certificate" {
    $certDir = "$repoRoot/src/Avelia.Shell.Windows"
    Push-Location $certDir
    try {
        winapp cert generate --if-exists Skip
    } finally {
        Pop-Location
    }
}

Step "Trust developer certificate (UAC prompt)" {
    $certPath = "$repoRoot/src/Avelia.Shell.Windows/devcert.pfx"
    if (-not (Test-Path $certPath)) {
        throw "devcert.pfx missing at $certPath — earlier cert generate step did not produce a file."
    }
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        winapp cert install $certPath
        return
    }
    Write-Host "    elevating to install certificate to LocalMachine store..."
    $pwshExe = (Get-Process -Id $PID).Path
    $proc = Start-Process -FilePath $pwshExe `
        -ArgumentList @("-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "winapp cert install '$certPath'; exit `$LASTEXITCODE") `
        -Verb RunAs `
        -Wait `
        -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "Elevated 'winapp cert install' exited with code $($proc.ExitCode)."
    }
}

Step "Restore local dotnet tools" {
    dotnet tool restore
}

Step "Restore NuGet packages" {
    dotnet restore Avelia.sln
}

Step "Build solution" {
    dotnet build Avelia.sln --no-restore -c Debug
}

if (-not $SkipTests) {
    Step "Run fast tests" {
        & "$repoRoot/scripts/test-fast.ps1"
    }
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host " Bootstrap complete." -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  Run the app:      winapp run src/Avelia.Shell.Windows"
Write-Host "  Run all tests:    ./scripts/test-all.ps1"
Write-Host "  Format code:      ./scripts/format.ps1"
Write-Host "  Read the docs:    docs/architecture.md"
Write-Host ""
