set windows-shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
set shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]

config := "Debug"
shell_out := "src/Avelia.Shell.Windows/bin/x64/" + config + "/net10.0-windows10.0.19041.0/win-x64"

# List recipes
default:
    @just --list

# Build the entire solution
build:
    dotnet build Avelia.sln -c {{config}}

# Run unit + property tests (no DB / no E2E / no perf)
test:
    ./scripts/test-fast.ps1

# Build the shell, then launch it under packaged identity via winapp
run: build
    winapp run {{shell_out}}

# Format F# (fantomas) and C# (csharpier) sources in place
fmt:
    ./scripts/format.ps1

# Verify formatting without writing — exits non-zero if any file would change
fmt-check:
    ./scripts/format.ps1 -Check
