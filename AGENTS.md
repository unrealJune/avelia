# Agent guidance

Generic instructions for any AI coding agent operating in this repo. Claude Code has additional guidance in `CLAUDE.md` — read both.

## Ground rules

- **Bootstrap is the source of truth.** If something needs to be set up to work, it goes in `scripts/bootstrap.ps1`. Don't write workarounds in docs.
- **Don't add features outside the requested scope.** No spec-uplift, no "I noticed and also fixed". Ask first.
- **Match the language of the project being edited.** F# in `Conductor.Core/*`, C# in `Conductor.Shell.Windows/*`. Don't introduce C# into F# projects or vice versa.
- **No mocked databases in integration tests.** Spin up a real SQLite/Postgres instance — mocks have masked prod migration bugs before.
- **Prefer existing scripts over ad-hoc commands.** `./scripts/test-fast.ps1`, not `dotnet test --filter ...`.

## Where things live

| Concern                  | Project                                   |
| ------------------------ | ----------------------------------------- |
| Domain types & rules     | `Avelia.Core` / `Avelia.Core.Abstractions` |
| Storage                  | `Avelia.Persistence`                       |
| GitHub integration       | `Avelia.Vcs.GitHub`                        |
| Claude Code orchestration| `Avelia.Agent.ClaudeCode`                  |
| WinUI 3 desktop shell    | `Avelia.Shell.Windows`                     |

ViewModels live with the shell. Pure logic lives in `*.Core*`.

## Editing rules

1. Read `Directory.Packages.props` before adding a NuGet package — versions are pinned centrally.
2. Run `./scripts/format.ps1` before committing.
3. Run `./scripts/test-fast.ps1` before pushing.
4. Use `winapp run` to launch the shell, not `dotnet run` — packaged identity matters for notifications/file-associations/deep-links.

## What CI runs

`./scripts/test-fast.ps1` on every push. `./scripts/test-all.ps1` on PRs targeting `main`. If your local result diverges from CI, run `./scripts/clean.ps1 && ./scripts/bootstrap.ps1` — your local state has drifted.
