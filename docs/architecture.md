# Architecture

Avelia is a single-process Windows desktop application: an F# domain core with a C# WinUI 3 shell. The boundary is small and disciplined so the same core can later host a different shell on macOS/Linux.

## Process model

```
+---------------------------- avelia.exe ----------------------------+
|                                                                    |
|  +--------------------+              +---------------------------+ |
|  |  WinUI 3 shell     |  commands -> |   F# core services        | |
|  |  (Avelia.Shell.    |              |   (Avelia.Core,           | |
|  |   Windows)         |  <- events   |    Avelia.Persistence,    | |
|  |                    |              |    Avelia.Vcs.GitHub,     | |
|  |  XAML + VMs        |              |    Avelia.Agent.ClaudeCode| |
|  +--------------------+              +---------------------------+ |
|                                                                    |
+--------------------------------------------------------------------+
```

The shell never touches storage, git, or the agent directly. It speaks to typed service interfaces (`ITaskService`, `IVcsService`, etc.) exposed by the core. The core never references `Microsoft.UI.Xaml`.

## Projects

| Project                          | Lang | Purpose                                                    |
| -------------------------------- | ---- | ---------------------------------------------------------- |
| `src/Avelia.Core.Abstractions`   | F#   | Interfaces, DTOs, error types. No implementations.         |
| `src/Avelia.Core`                | F#   | Domain types, state machines, orchestration.               |
| `src/Avelia.Persistence`         | F#   | Storage adapter (SQLite by default).                       |
| `src/Avelia.Vcs.GitHub`          | F#   | GitHub API + local git operations.                         |
| `src/Avelia.Agent.ClaudeCode`    | F#   | Claude Code subprocess driver.                             |
| `src/Avelia.Shell.Windows`       | C#   | WinUI 3 application, ViewModels, XAML.                     |

Tests mirror the source tree under `tests/`.

## Threading

- Core services are thread-safe and dispatcher-agnostic.
- Shell marshals back to the UI thread via `DispatcherQueue` when reacting to subscription events.
- No `Async.RunSynchronously`, `.Result`, or `.Wait()` anywhere.

## Persistence

SQLite file under `%LOCALAPPDATA%/Avelia/avelia.db`. Storage is the source of truth: every state change is persisted, in-memory state is a cache hydrated on startup.

## Cross-process boundaries

- **Claude Code** runs as a child process; we communicate via stdio + structured events.
- **Git** runs as a subprocess via `git.exe`; we shell out, never link libgit2 directly.
- **GitHub API** is plain HTTPS with PAT auth, no GitHub CLI dependency.

## Future plugin model

Plugins are not v1. The service interfaces are designed so a plugin runtime (out-of-process, JSON-RPC over stdio) can be slotted in later without restructuring the core.
