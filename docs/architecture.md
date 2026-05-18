# Architecture

Avelia is a single-process Windows desktop application: an F# domain core with a C# WinUI 3 shell. The boundary is small and disciplined so the same core can later host a different shell on macOS/Linux.

## Process model

```
+---------------------------- avelia.exe -----------------------------+
|                                                                     |
|  +--------------------+               +----------------------------+|
|  |  WinUI 3 shell     |  commands  -> |   F# core services         ||
|  |  (Avelia.Shell.    |               |   (Avelia.Core,            ||
|  |   Windows)         |  <- events    |    Avelia.Persistence,     ||
|  |                    |               |    Avelia.Vcs.Git,         ||
|  |  XAML + VMs        |               |    Avelia.Vcs.GitHub,      ||
|  |  ConPTY +          |               |    Avelia.Agent.ClaudeCode,||
|  |  xterm.js renderer |               |    Avelia.Agent.Copilot)   ||
|  +--------------------+               +----------------------------+|
|                                                                     |
+---------------------------------------------------------------------+
        |                        |                       |
        v                        v                       v
    git.exe              Node sidecar             Copilot CLI server
    (mutations)        (Claude Agent SDK)       (managed by .NET SDK)
```

The shell never touches storage, git, or the agent directly. It speaks to typed service interfaces (`IRepositoryService`, `IWorkspaceService`, `IConversationService`, `IAgentSession`, `IGitOperations`, `IGitHubClient`, `ITerminalSession`, etc.) exposed by the core. The core never references `Microsoft.UI.Xaml`.

## Projects

| Project                          | Lang | Purpose                                                            |
| -------------------------------- | ---- | ------------------------------------------------------------------ |
| `src/Avelia.Core.Abstractions`   | F#   | Interfaces, DTOs, error types. No implementations.                 |
| `src/Avelia.Core`                | F#   | Domain types, state machines, orchestration.                       |
| `src/Avelia.Persistence`         | F#   | Storage adapter (SQLite by default).                               |
| `src/Avelia.Vcs.Git`             | F#   | Local git operations (`git.exe` for mutations, LibGit2Sharp reads).|
| `src/Avelia.Vcs.GitHub`          | F#   | GitHub API client + auth (Octokit.NET, Device Flow, credential store).|
| `src/Avelia.Agent.ClaudeCode`    | F#   | Claude driver — bundled Node sidecar running `@anthropic-ai/claude-agent-sdk`. |
| `src/Avelia.Agent.Copilot`       | F#   | Copilot driver — direct `GitHub.Copilot.SDK` NuGet reference.      |
| `src/Avelia.Shell.Windows`       | C#   | WinUI 3 application, ViewModels, XAML, ConPTY + terminal renderer. |

Tests mirror the source tree under `tests/`.

## Threading

- Core services are thread-safe and dispatcher-agnostic.
- Shell marshals back to the UI thread via `DispatcherQueue` when reacting to subscription events.
- No `Async.RunSynchronously`, `.Result`, or `.Wait()` anywhere.

## Persistence

SQLite file under `%LOCALAPPDATA%/Avelia/avelia.db`. Storage is the source of truth: every state change is persisted, in-memory state is a cache hydrated on startup.

## Cross-process boundaries

- **Claude** runs through a bundled Node sidecar (`claude-host.mjs`) that uses `@anthropic-ai/claude-agent-sdk` and exposes it over JSON-RPC stdio. Node 20+ ships in the installer (`assets/runtime/node/`). The sidecar's underlying `claude` binary is auto-bundled by the SDK as an optional dep.
- **Copilot** runs through the official `GitHub.Copilot.SDK` NuGet package (tracking `1.0.0-beta.4`); the SDK manages its own JSON-RPC subprocess to the Copilot CLI server.
- **Interactive terminal mode** for both agents bypasses the SDK and hosts the underlying CLI (`claude` / `copilot`) directly in a ConPTY. Same on-disk session files in both modes, so users can switch fluidly.
- **Git** is hybrid: `git.exe` subprocess for mutating ops (commit, push, worktree add — respects user's signing, hooks, LFS, includeIf); LibGit2Sharp 0.31 for read-only inspection (status polling, ahead/behind, log) where per-op process cost matters. Per-repo `AsyncLock` serializes mutations across worktrees.
- **GitHub API** uses Octokit.NET 14 (REST) plus Octokit.GraphQL.NET (beta) for the dashboard's batched PR-list query. ETag caching middleware on the REST path.
- **GitHub auth** is GitHub App + OAuth Device Flow primary, OAuth App + Device Flow as GHES fallback, PAT entry as enterprise fallback. Tokens live in Windows Credential Manager (DPAPI-backed) behind an `ICredentialStore` interface.

See `docs/plans/backend.md` for the full backend implementation plan.

## Future plugin model

Plugins are not v1. The service interfaces are designed so a plugin runtime (out-of-process, JSON-RPC over stdio) can be slotted in later without restructuring the core.
