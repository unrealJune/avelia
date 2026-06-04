#Requires -Version 7.0
<#
.SYNOPSIS
    Vendor xterm.js + the fit and WebGL addons into the shell's terminal assets.

.DESCRIPTION
    The terminal renderer (backend plan B-7) hosts xterm.js inside a WebView2.
    The library is fetched here rather than committed so the repo stays free of
    large vendored bundles; run this once after clone (and after bumping the
    pinned versions below). Output lands in
    src/Avelia.Shell.Windows/Assets/terminal/vendor/, which the .csproj packages
    as Content.
#>
[CmdletBinding()]
param(
    [string]$XtermVersion = "5.5.0",
    [string]$FitVersion = "0.10.0",
    [string]$WebglVersion = "0.18.0"
)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $repoRoot "src/Avelia.Shell.Windows/Assets/terminal/vendor"
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

$cdn = "https://cdn.jsdelivr.net/npm"
$files = @(
    @{ Url = "$cdn/@xterm/xterm@$XtermVersion/lib/xterm.js"; Name = "xterm.js" }
    @{ Url = "$cdn/@xterm/xterm@$XtermVersion/css/xterm.css"; Name = "xterm.css" }
    @{ Url = "$cdn/@xterm/addon-fit@$FitVersion/lib/addon-fit.js"; Name = "addon-fit.js" }
    @{ Url = "$cdn/@xterm/addon-webgl@$WebglVersion/lib/addon-webgl.js"; Name = "addon-webgl.js" }
)

foreach ($f in $files) {
    $dest = Join-Path $vendor $f.Name
    Write-Host "Fetching $($f.Name) ..."
    Invoke-WebRequest -Uri $f.Url -OutFile $dest
}

Write-Host "Vendored xterm $XtermVersion (+ fit $FitVersion, webgl $WebglVersion) into $vendor" -ForegroundColor Green
