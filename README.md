# Avelia

WinUI 3 desktop shell with F# core services. See `docs/architecture.md` for the high-level picture.

## Quick start

```powershell
git clone https://github.com/<you>/avelia
cd avelia
./scripts/bootstrap.ps1
```

That installs every prerequisite (PowerShell 7, .NET SDK, `winapp` CLI, WinUI templates, dev cert), restores packages, builds, and runs the fast tests. On a fresh Windows 11 box it takes under 15 minutes.

## Daily commands

| What                 | How                                              |
| -------------------- | ------------------------------------------------ |
| Run the app          | `winapp run src/Avelia.Shell.Windows`            |
| Build                | `./scripts/build.ps1`                            |
| Fast tests           | `./scripts/test-fast.ps1`                        |
| Integration tests    | `./scripts/test-integration.ps1`                 |
| E2E tests            | `./scripts/test-e2e.ps1`                         |
| All tests            | `./scripts/test-all.ps1`                         |
| Format               | `./scripts/format.ps1`                           |
| Clean                | `./scripts/clean.ps1`                            |

## Layout

```
src/   F# core + C# WinUI shell
tests/ Matching test projects per src/ project
docs/  Architecture and protocol docs
scripts/  One-shot scripts (bootstrap / build / test / format / clean)
```

See `docs/architecture.md` for a per-project breakdown.

## Contributing

Onboarding is one command: `./scripts/bootstrap.ps1`. If that fails on a clean machine, the script is buggy — fix the script, don't paper over it in the docs.

Read `AGENTS.md` (generic) or `CLAUDE.md` (Claude Code specific) before letting an AI agent loose on the repo.
