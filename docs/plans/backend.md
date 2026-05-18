# Avelia — Backend Implementation Plan

## Context

The Fluent shell (`winui-conductor-fluent.md`) ships against stub services in `Avelia.Core.Stubs`. That plan deferred "Chunk 10 — Real backend" as out of scope for v1. This plan covers Chunk 10 in detail: replacing the stubs with real agent drivers, local git operations, GitHub integration, terminal hosting, and persistence.

Today the agent and VCS projects exist as near-empty placeholders:

- `Avelia.Agent.ClaudeCode/ClaudeCode.fs` — one `AgentSettings` record.
- `Avelia.Vcs.GitHub/GitHub.fs` — one `RepoCoordinate` parser.

The typed service interfaces in `Avelia.Core.Abstractions/Services.fs` (`IRepositoryService`, `IWorkspaceService`, `IConversationService`, `IDiffService`, `IPullRequestService`, `IInboxService`, `ISettingsService`) already define the shell-facing contract. They are not yet enough for the backend — they describe **what the shell reads**, not **how the agent runs, how the terminal streams, or how git/GitHub plumbing is shaped**. This plan introduces those missing contracts.

## Decisions (locked in with user, 2026-05-18)

1. **Claude integration: bundled Node sidecar.** Avelia ships Node 20+ in the installer (~50 MB cost). A small sidecar script (`claude-host.mjs`, ~150 LoC) uses `@anthropic-ai/claude-agent-sdk` and exposes its `query()` async iterator over JSON-RPC stdio. F# core spawns one sidecar per session.
2. **Copilot integration: direct .NET SDK.** Take a NuGet dep on `GitHub.Copilot.SDK` (track `1.0.0-beta.4`). The SDK manages its own JSON-RPC subprocess to the Copilot CLI server internally.
3. **Local git: hybrid.** `git.exe` for mutating ops (commit, push, worktree add — respects user's signing/hooks/LFS); LibGit2Sharp 0.31 for read-only inspection (status, ahead/behind, log) where polling costs matter.
4. **GitHub auth: GitHub App + OAuth Device Flow** primary; PAT entry fallback for enterprise users. Tokens in Windows Credential Manager.
5. **GitHub API: Octokit.NET 14** (REST) for everything; **Octokit.GraphQL.NET** (beta) only for the batched dashboard "PRs with checks + reviews" query.
6. **Terminal: ConPTY via in-house P/Invoke; xterm.js + `@xterm/addon-webgl` inside a single shared WebView2.** Skip `EasyWindowsTerminalControl.WinUI` (alpha/unofficial; no advantage over WebView2). Skip Pty.Net (unmaintained).
7. **TUI persistence: asciicast v2.** Append-only JSONL of `[time, "o", "bytes"]` per session.
8. **Two run modes per agent:** `Headless` (SDK-driven, events streamed into our chat UI) and `Interactive` (CLI hosted in a ConPTY for the terminal panel). Both modes read/write the same on-disk session files (`~/.claude/projects/...`, `~/.copilot/session-state/...`) so a user can fluidly switch.

## Architecture deltas vs `docs/architecture.md`

The existing architecture doc is amended in the same PR as this plan. Specifically:

| Topic | Was | Now |
|---|---|---|
| Local git | "Subprocess via `git.exe`; never link libgit2 directly" | Hybrid (CLI mutations + LibGit2Sharp reads) |
| GitHub auth | "PAT auth, no GitHub CLI dependency" | GitHub App + Device Flow primary; PAT fallback |
| Agent drivers | One project, `Avelia.Agent.ClaudeCode` | Add `Avelia.Agent.Copilot`; both implement `IAgentSession` |
| Local git project | (Not listed) | New `Avelia.Vcs.Git` for git.exe + libgit2 wrappers (separate from `Avelia.Vcs.GitHub` which becomes GitHub-API-only) |
| Terminal | (Not listed) | New `ITerminalSession` in core; Windows ConPTY impl in shell |

The "single-process, F# core no UI deps" rule is unchanged. All sidecar processes (Node for Claude, the SDK's internal CLI server for Copilot, `git.exe`) live outside the .NET process and are driven via stdio.

## Pillar 1 — Agent drivers

### Shared shape: `IAgentSession` + mode split

One base interface, two specializations (headless vs interactive), one factory. Two driver implementations (Claude, Copilot). All in `Avelia.Core.Abstractions`. Lifecycle is owned by the factory — by the time you have an interface, the session is running.

Conventions enforced at the boundary: `OperationResult<'T>` (not raw `Result`); no `'T option` (empty/zero/`""` sentinels); every new DU exposes a `.Match` visitor for C# (mirrors `MessageEvent.Match` / `ModelChoice.Match`).

```fsharp
[<RequireQualifiedAccess>]
type PermissionMode =
    | AcceptEdits        // auto-approve file writes (SDK default for headless)
    | RequireApproval    // every tool call asks the host via PermissionRequired
    | ReadOnly           // reject any mutation tool
    | Plan               // Claude "plan" mode — read-only + propose

type PermissionDecision = Allow | Deny | AllowAlways

type PermissionRequest = {
    RequestId: Guid
    ToolName: string
    ToolInputJson: string
    Description: string
}

type CostSnapshot = {
    InputTokens: int
    OutputTokens: int
    /// 1e-6 USD. Avoids float at the boundary; 6 decimal places of precision.
    CostMicroUsd: int64
}

type McpServerConfig = {
    Command: string
    Args: string array
    Env: IReadOnlyDictionary<string, string>
}

type AgentSessionConfig = {
    Workspace: RepoPath              // reuses existing primitive (worktree root)
    Model: ModelChoice               // required; no nullable
    SystemPromptAppend: string       // "" = no append
    AllowedTools: string array       // [||] = SDK default
    PermissionMode: PermissionMode
    McpServers: IReadOnlyDictionary<string, McpServerConfig>
    ResumeSessionId: string          // "" = new session
}

/// Events emitted by a headless session. Wraps the existing MessageEvent for
/// chat events (re-using UserMessage / AgentMessage / ToolBatch / etc.) and
/// adds session-lifecycle cases for non-conversation signals.
[<RequireQualifiedAccess>]
type AgentEvent =
    | Initialized of sessionId:string * model:ModelChoice
    | Conversation of MessageEvent                          // re-uses existing union
    | CostUpdated of snapshot:CostSnapshot                  // mid-flight; best-effort (Copilot streams, Claude doesn't)
    | PermissionRequired of request:PermissionRequest       // host replies via RespondToPermissionAsync
    | RetryAttempt of attempt:int * delayMs:int * reason:string
    | Warning of message:string
    | Ended of exitCode:int * totals:CostSnapshot           // always emitted; stream completes after

// Base — common lifecycle, both modes
type IAgentSession =
    inherit IAsyncDisposable
    abstract SessionId : SessionId
    abstract Workspace : RepoPath
    abstract InterruptAsync : CancellationToken -> Task
    abstract WaitForExitAsync : CancellationToken -> Task<int>

// Headless — SDK-driven, events stream into chat UI
type IHeadlessAgentSession =
    inherit IAgentSession
    abstract Events : CancellationToken -> IAsyncEnumerable<AgentEvent>
    abstract SendUserMessageAsync :
        text:string * refs:string array * CancellationToken
        -> Task<OperationResult<unit>>
    abstract RespondToPermissionAsync :
        requestId:Guid * decision:PermissionDecision * CancellationToken
        -> Task<OperationResult<unit>>

// Interactive — CLI in ConPTY, bytes stream into terminal panel
type IInteractiveAgentSession =
    inherit IAgentSession
    abstract Terminal : ITerminalSession

type IAgentSessionFactory =
    abstract StartHeadlessAsync :
        AgentSessionConfig * CancellationToken
        -> Task<OperationResult<IHeadlessAgentSession>>
    abstract StartInteractiveAsync :
        AgentSessionConfig * CancellationToken
        -> Task<OperationResult<IInteractiveAgentSession>>
```

Per-driver `IAgentSessionFactory` registrations (one for Claude, one for Copilot) plug into Composition. The shell selects via configuration; the chat projection layer never sees vendor-specific types because every driver maps its native events to `AgentEvent` at its own boundary.

**Error policy.** Crossing the boundary, everything is `OperationResult<'T>` with `AveliaError`. Internal driver code may use richer DUs (`AgentError`, `GitError`) for precise pattern-matching; map them to `AveliaError` at the public surface. Add a new `AveliaError.External of source:string * detail:string` case in `Errors.fs` for SDK-surfaced failures that don't fit Network / Validation / Unauthorized / Conflict / NotFound / Internal.

### Claude — Node sidecar

**Layout.** Sidecar script ships under `assets/agents/claude-host/` and gets copied into the MSIX package. Node runtime ships under `assets/runtime/node/` (bundled, not from PATH).

**Protocol.** JSON-RPC over stdio. The sidecar exposes four methods (`session.start`, `session.send`, `session.interrupt`, `session.dispose`) and emits one notification stream (`session.event`) carrying the canonical `AgentEvent` shape pre-mapped on the Node side. Keeping the mapping in the sidecar means the F# core never parses Anthropic-specific types.

**Why a sidecar instead of `claude --print`.** The TS SDK gives us hooks (PreToolUse/PostToolUse/SessionStart callbacks invoked back into our process), subagents, programmatic `setting_sources` control, and structured permission callbacks. Reimplementing those against the CLI's stream-json would re-litigate decisions the SDK has already made.

**Auth.** Driven entirely by env vars on the sidecar process — `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`. Avelia's onboarding flow captures these into the credential store and injects them per-spawn. No claude.ai OAuth in our app (Anthropic's branding terms forbid it for third parties).

**Bundling cost.** Node 20.x Windows zip extracts to ~50 MB. We strip `npm` and `corepack` from the bundled tree (~15 MB savings). The Anthropic SDK auto-bundles the Claude Code native binary as an optional dep so users don't need a separate `claude` install.

### Copilot — direct .NET SDK

**Layout.** New project `Avelia.Agent.Copilot` references `GitHub.Copilot.SDK` 1.0.0-beta.4. The driver wraps SDK types in our `IAgentSession`.

**Why direct.** The SDK is GitHub-published, ships on NuGet (~390k downloads, verified publisher), targets .NET 8+. No reason to wrap it in a sidecar.

**Auth.** SDK reads `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN`. Same Avelia-managed token vault as the GitHub VCS layer — we reuse the user's GitHub App token where the SDK accepts it, falling back to PAT.

**Risk.** Preview SDK. We pin the version and shield our domain from SDK type churn by mapping eagerly to `AgentEvent` at the boundary. If a beta bump breaks us, only the driver project needs to change.

### Interactive mode

Both drivers, in `Interactive` mode, **bypass the SDK entirely** and spawn the underlying CLI directly in a ConPTY:

- Claude: `claude` binary (auto-detected; if missing, point the user at install instructions).
- Copilot: `copilot` binary (same).

The driver still owns the `IAgentSession` lifecycle and exposes the `ITerminalSession` for the shell's terminal panel. The chat-UI events stream is empty in interactive mode (the terminal IS the UI).

Because both CLIs use the same on-disk session files in both modes, a user can:
- Start a task headlessly, watch progress in chat, then pop the terminal to take over interactively.
- Or start in terminal, then close the panel and let the agent continue headlessly.

We surface this as a single "Mode" toggle per session.

## Pillar 2 — Local git + GitHub

### Local git operations: `Avelia.Vcs.Git`

Two interfaces in `Avelia.Core.Abstractions`, one impl project. Worktree paths reuse the existing `RepoPath` primitive — a worktree IS a working-tree root, no new path type needed. Status folds branch / ahead-behind / file-list into one snapshot (cheaper than four separate .git reads).

```fsharp
type CommitId =
    | CommitId of string                  // raw SHA hex; never Guid
    member this.Value = let (CommitId s) = this in s

type CommitMessage = CommitMessage of string
type Remote = Remote of string            // "origin", etc.

type Worktree = {
    Path: RepoPath
    Branch: BranchName
    Head: CommitId
    IsLocked: bool
}

type AheadBehind = { Ahead: int; Behind: int }

type WorkingTreeFileStatus = {
    Path: RelativePath
    IsModified: bool
    IsStaged: bool
    IsUntracked: bool
    IsConflicted: bool
}

type WorktreeStatus = {
    Branch: BranchName
    AheadBehind: AheadBehind
    Files: WorkingTreeFileStatus array
    HasUncommittedChanges: bool          // derived for cheap reads at the boundary
}

type CommitInfo = {
    Id: CommitId
    Author: string
    AuthoredAt: DateTimeOffset
    Subject: string
}

type IGitOperations =                    // mutating -> git.exe
    abstract WorktreeAddAsync :
        repo:RepoPath * branch:BranchName * worktree:RepoPath * CancellationToken
        -> Task<OperationResult<Worktree>>
    abstract WorktreeRemoveAsync :
        worktree:RepoPath * force:bool * CancellationToken
        -> Task<OperationResult<unit>>
    abstract CommitAsync :
        worktree:RepoPath * message:CommitMessage * CancellationToken
        -> Task<OperationResult<CommitId>>
    abstract PushAsync : worktree:RepoPath * remote:Remote * CancellationToken -> Task<OperationResult<unit>>
    abstract FetchAsync : worktree:RepoPath * remote:Remote * CancellationToken -> Task<OperationResult<unit>>
    abstract CheckoutAsync : worktree:RepoPath * branch:BranchName * CancellationToken -> Task<OperationResult<unit>>
    abstract BranchCreateAsync :
        repo:RepoPath * branch:BranchName * baseRef:BranchName * CancellationToken
        -> Task<OperationResult<unit>>
    abstract BranchDeleteAsync : repo:RepoPath * branch:BranchName * CancellationToken -> Task<OperationResult<unit>>

type IGitInspection =                    // read-only -> LibGit2Sharp (CLI fallback)
    abstract StatusAsync : worktree:RepoPath * CancellationToken -> Task<OperationResult<WorktreeStatus>>
    abstract LogAsync :
        worktree:RepoPath * limit:int * CancellationToken
        -> Task<OperationResult<IReadOnlyList<CommitInfo>>>
    abstract ListBranchesAsync :
        repo:RepoPath * CancellationToken
        -> Task<OperationResult<IReadOnlyList<BranchName>>>
    abstract ListWorktreesAsync :
        repo:RepoPath * CancellationToken
        -> Task<OperationResult<IReadOnlyList<Worktree>>>
```

All inspection methods are async even though LibGit2Sharp is sync internally — gives us cancellation support and lets a future driver swap to true async I/O without surface churn.

**Subprocess discipline.** `System.Diagnostics.Process`, `UseShellExecute = false`, `RedirectStandardOutput/Error = true`. Force `LC_ALL=C.UTF-8` and `GIT_TERMINAL_PROMPT=0` (never block on a credential prompt — Git Credential Manager handles auth out-of-band). Parse `--porcelain=v2 -z` for status, `--format=%H%x00...` for log. **Never parse human-readable output.**

**Concurrency.** Serialize mutating ops **per repository** (not per worktree) via an `AsyncLock` keyed on canonical repo path — `.git/packed-refs` and the object DB are shared across worktrees and two concurrent commits in different worktrees can race. Reads can proceed in parallel.

**Startup checks.** On first launch in a repo, verify `core.longpaths=true` (warn if missing — Avelia worktree paths plus `.git/worktrees/<n>/` plus deep source files can blow MAX_PATH). Offer a one-click fix.

### GitHub API: `Avelia.Vcs.GitHub`

Replaces the placeholder `GitHub.fs` with:

**Scope.** `IGitHubClient` lives **inside `Avelia.Vcs.GitHub`**, not in `Avelia.Core.Abstractions`. The shell talks to the existing high-level services (`IRepositoryService`, `IPullRequestService`, `IInboxService`) which the GitHub project implements on top of this client. Keeping it internal stops the Octokit / Octokit.GraphQL.NET (beta) dependency surface from leaking into the public abstraction layer.

Shape (signatures use `OperationResult<'T>` and concrete sentinels per project convention; `Match` visitors on any new DUs):

```fsharp
type RepoSummary = { Owner: string; Name: string; DefaultBranch: BranchName; IsPrivate: bool }
type CreatePrRequest = { Repo: RepoCoordinate; Title: string; Body: string; Head: BranchName; Base: BranchName }
type Notification = { Id: string; RepoFullName: string; Subject: string; Reason: string; UpdatedAt: DateTimeOffset }

type internal IGitHubClient =
    abstract ListReposAsync : CancellationToken -> Task<OperationResult<IReadOnlyList<RepoSummary>>>
    abstract GetPrAsync :
        repo:RepoCoordinate * prNumber:int * CancellationToken
        -> Task<OperationResult<PullRequest>>
    /// GraphQL one-shot: PRs + checks + reviews in one round-trip for the dashboard.
    abstract ListPrsForUserAsync : CancellationToken -> Task<OperationResult<IReadOnlyList<PullRequest>>>
    abstract CreatePrAsync : request:CreatePrRequest * CancellationToken -> Task<OperationResult<PullRequest>>
    abstract CommentAsync :
        repo:RepoCoordinate * prNumber:int * body:string * CancellationToken
        -> Task<OperationResult<unit>>
    /// `since` of `DateTimeOffset.MinValue` means "everything"; matches existing
    /// "empty sentinel" convention.
    abstract ListNotificationsAsync :
        since:DateTimeOffset * CancellationToken
        -> Task<OperationResult<IReadOnlyList<Notification>>>
```

**REST via Octokit.NET 14** for individual ops. Set `ApiOptions { PageSize = 100, PageCount = int.MaxValue }` or use `Octokit.AsyncPaginationExtension` — never iterate `GetAllForCurrent()` with defaults (silent truncation has bitten people).

**GraphQL via Octokit.GraphQL.NET** only for the dashboard's PR list (one round-trip vs 76 REST calls). Accepting that this lib is still 0.4-beta is the trade we make for one well-bounded query.

**Rate-limit handling.** Read `client.GetLastApiInfo().RateLimit` after every call. Below 500 remaining, back off. Catch `RateLimitExceededException` and `SecondaryRateLimitExceededException`; honor `Retry-After`. **Per-URL ETag caching middleware** via a custom `HttpMessageHandler` plugged into Octokit's `IHttpClient` slot — `If-None-Match` / `If-Modified-Since` 304s don't count against rate limit.

### Auth: `Avelia.Vcs.GitHub.Auth`

Three paths, in priority order:

1. **GitHub App + Device Flow (primary).** User clicks "Sign in to GitHub" → device code → enters on `github.com/login/device` → approves repo access. Token is short-lived user-to-server (8h) with 6mo refresh. Required permissions (GitHub App):

   - Contents: read & write (clone, push)
   - Pull requests: read & write
   - Issues: read
   - Metadata: read (always required)
   - Checks: read
   - Commit statuses: read
   - **Not** Workflows: write (avoid unless we ever modify `.github/workflows/*`)

2. **OAuth App + Device Flow (fallback for GHES).** Same flow, OAuth scopes `repo` + `read:user`. Used when a user's enterprise disallows GitHub Apps.

3. **PAT entry (fallback for locked-down enterprises).** Paste a token. Accept both classic and fine-grained PATs. Document the required permissions in the UI.

**Token storage.** Windows Credential Manager via `Meziantou.Framework.Win32.CredentialManager` (or thin P/Invoke around `CredWrite`/`CredRead`). Target name `avelia:github:<account-login>`. Inspectable / revocable from Control Panel → Credential Manager. Same store git-credential-manager uses, no confusion. Hidden behind an `ICredentialStore` interface in core so future macOS Keychain / Linux libsecret implementations slot in.

### Event subscriptions

No webhook endpoint (desktop app). Polling pattern:

- Watched PRs (sessions in flight) — `GET /repos/{o}/{r}/pulls/{n}` every 30–60s with cached ETag.
- Notifications inbox — `GET /notifications` every 60s honoring `X-Poll-Interval`. Cheapest path to "PR #N got merged" (filter `reason: subject_merged`).
- Background repo + PR list sync — every 5–10min, ETag-cached.

At 10 watched PRs + 1 inbox/min, ~800 req/h before 304s — well under 5000/h cap.

## Pillar 3 — Terminal hosting

### `ITerminalSession`

In `Avelia.Core.Abstractions` (no UI deps). Bytes in, bytes out. Size carried as a record so call sites and event payloads share one shape:

```fsharp
type TerminalSize = { Cols: int; Rows: int }

type TerminalExit = {
    ExitCode: int
    /// True when the child exited on its own; false if killed by InterruptAsync,
    /// process termination, or a host crash.
    IsClean: bool
}

type ITerminalSession =
    inherit IAsyncDisposable
    abstract Size : TerminalSize
    abstract WriteAsync : bytes:ReadOnlyMemory<byte> * CancellationToken -> Task
    /// Bytes from the child's stdout/stderr (combined). Single-consumer; the
    /// enumerator completes when the child exits or the token is cancelled.
    abstract ReadAllAsync : CancellationToken -> IAsyncEnumerable<ReadOnlyMemory<byte>>
    abstract ResizeAsync : size:TerminalSize * CancellationToken -> Task
    /// Writes 0x03 to the input pipe; ConPTY converts to CTRL_C_EVENT for the
    /// child's process group. Property test in B-6 asserts the round-trip.
    abstract SendInterruptAsync : CancellationToken -> Task
    abstract WaitForExitAsync : CancellationToken -> Task<TerminalExit>
```

### Windows impl: ConPTY P/Invoke

Lives in `Avelia.Shell.Windows/Terminal/ConPtySession.cs`. ~300 LoC wrapping `CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole`, plus `STARTUPINFOEX` + `CreateProcessW` to launch the child with the pseudo-console attached. Reads/writes go through anonymous-pipe `FileStream`s, naturally async.

**Ctrl+C.** Write byte `0x03` into the input pipe; ConPTY translates to `CTRL_C_EVENT` for the child's process group. Property test asserts this.

**Why not Pty.Net.** Last commit May 2024, no releases. We own ~300 lines or we pin to a dormant dep — own it.

### Renderer: xterm.js in WebView2

`TerminalView` (XAML UserControl) hosts a single `Microsoft.UI.Xaml.Controls.WebView2`. The WebView2 navigates to a packaged `terminal.html` that bundles xterm.js + `@xterm/addon-webgl`. Multiple terminal sessions = multiple xterm.js instances inside that one WebView2 (tabs), **not** multiple WebView2s.

**Data path.** ConPTY output → batched on an ~8ms timer → `CoreWebView2.SharedBufferRequested` SharedArrayBuffer → xterm.js `write()`. Avoids the JSON-stringify tax of `postMessage`.

**Performance bar.** 60fps streaming of Claude Code output, 24-bit color, mouse, IME, copy/paste, OSC8 hyperlinks. WebGL renderer hits this comfortably.

**Lifecycle.** WebView2 environment is warmed at app start (`CoreWebView2Environment.CreateAsync` on the splash screen) so the first terminal open isn't gated on cold WebView2 init.

### Persistence: asciicast v2

One `.cast` file per session under `%LOCALAPPDATA%/Avelia/sessions/<session-id>.cast`. JSON header line + newline-delimited `[time, "o", "bytes"]` arrays, append-only, real-time safe. On session reopen: replay the cast into xterm.js as fast as possible to rebuild scrollback, then attach the live ConPTY. Cap at 100 MB with rotation.

## Service interface refinements

The current `Services.fs` covers the shell's read side. The backend needs additional contracts:

In `Avelia.Core.Abstractions`:
- `IAgentSession` / `IHeadlessAgentSession` / `IInteractiveAgentSession` (above) — per-session driver.
- `IAgentSessionFactory` (above) — `StartHeadlessAsync` / `StartInteractiveAsync`. One factory registered per agent kind in Composition.
- `IGitOperations` / `IGitInspection` (above).
- `ITerminalSession` (above).
- `ICredentialStore` — credential vault behind a small interface so future macOS/Linux backends slot in:
  ```fsharp
  type ICredentialStore =
      abstract GetAsync : key:string * CancellationToken -> Task<OperationResult<string>>
      abstract SetAsync : key:string * secret:string * CancellationToken -> Task<OperationResult<unit>>
      abstract DeleteAsync : key:string * CancellationToken -> Task<OperationResult<unit>>
  ```
- `ISessionPersistence` / `IAsciiCastWriter` — asciicast v2 record/replay:
  ```fsharp
  type IAsciiCastWriter =
      inherit IAsyncDisposable
      abstract AppendAsync :
          bytes:ReadOnlyMemory<byte> * elapsed:TimeSpan * CancellationToken
          -> Task

  type ISessionPersistence =
      abstract OpenWriterAsync :
          sessionId:SessionId * CancellationToken
          -> Task<OperationResult<IAsciiCastWriter>>
      abstract ReplayAsync :
          sessionId:SessionId
          * sink:Func<ReadOnlyMemory<byte>, ValueTask>
          * CancellationToken
          -> Task<OperationResult<unit>>
  ```

Internal to `Avelia.Vcs.GitHub` (not in Abstractions):
- `IGitHubClient` (above). The Octokit / Octokit.GraphQL.NET surface stays behind it.

`Errors.fs`:
- Add `AveliaError.External of source:string * detail:string` for SDK-surfaced failures that don't fit existing cases. Update `AveliaError.Match` accordingly.

Existing legacy interfaces in `Services.fs` (`ITaskService`, `IVcsService`, `IAgentService`) are deleted in the same PR — nothing references them after the design-driven services landed.

## Testing strategy

Per CLAUDE.md test tiers and the existing PBT bar:

**Property tests:**
- `AgentEvent` mapping: `roundtrip(mapToCanonical(claudeNativeEvent)) = mapToCanonical(claudeNativeEvent)` (idempotent), same for Copilot.
- asciicast: `replay(serialize(stream)) = stream`.
- Git path handling: worktree paths never escape repo root.
- Auth token serialization: roundtrip through credential store.

**Contract tests:**
- One suite per agent run mode: `IHeadlessAgentSession` (factory hands one back; sending a no-tool prompt yields `AgentEvent.Conversation` events; `InterruptAsync` produces an `Ended` event with non-zero exit; resume on the same `SessionId` re-attaches to on-disk session) and `IInteractiveAgentSession` (factory hands one back; `Terminal.WriteAsync` round-trips bytes; `InterruptAsync` causes a clean `TerminalExit.IsClean = false`).
- One suite against every `IGitOperations` impl (CLI implementation + a future libgit2 mutation impl if we ever add one).

**Integration tests:**
- Real `git.exe` against a real temp repo. Worktree add/remove/list round-trip.
- Real Octokit against a test GitHub App on a sandbox repo.
- Real ConPTY hosting `cmd.exe /c "echo hi"` and asserting the bytes round-trip.

**E2E:**
- Launch the app, start a Claude headless session against a fake Anthropic endpoint (mock at the HTTP layer), assert chat UI streams text deltas.
- Same, but interactive — assert the terminal panel mounts and bytes flow.

**Snapshot tests:**
- `AgentEvent` JSON snapshots so vendor SDK upgrades don't silently change our chat-projection shape.

## Implementation chunks

Backend rollout is reviewable in chunks the same way the shell was. Initial sketch (subject to revision once we start):

| Chunk | Subject | Notes |
|------:|:------|:------|
| B-0 | Service-contract extension | Land `IAgentSession` + `IHeadlessAgentSession` + `IInteractiveAgentSession` + `IAgentSessionFactory`, `IGitOperations`, `IGitInspection`, `ITerminalSession`, `ICredentialStore`, `ISessionPersistence` + new primitives (`CommitId`, `CommitMessage`, `Remote`, `Worktree`, `WorktreeStatus`, `TerminalSize`, etc.) in `Avelia.Core.Abstractions`. Add `AveliaError.External` case. Delete legacy `ITaskService`/`IVcsService`/`IAgentService`. No impls yet — stubs continue to satisfy the shell. |
| B-1 | Local git CLI driver | `Avelia.Vcs.Git.GitCli` — worktree add/remove/list, commit, push, fetch, checkout via `git.exe`. Per-repo `AsyncLock`. Long-paths check on startup. Integration tests against temp repos. |
| B-2 | Local git inspection driver | `Avelia.Vcs.Git.GitInspector` — LibGit2Sharp 0.31 wrapper. Status, ahead/behind, log, branches, worktrees. Falls back to CLI on `unsupported repository version` (sparse, partial clone). |
| B-3 | GitHub auth | Device Flow + GitHub App + PAT fallback. `Avelia.Vcs.GitHub.Auth`. Windows Credential Manager via `ICredentialStore`. Onboarding UI for first-run sign-in. |
| B-4 | GitHub API client | Octokit.NET REST surface. ETag caching middleware. Rate-limit handling. Polling loops behind `IEventStream`. |
| B-5 | GitHub dashboard query | Octokit.GraphQL.NET — PR list with checks + reviews. One batched query for the dashboard view. |
| B-6 | ConPTY layer | `ConPtySession` P/Invoke. Process spawn, resize, Ctrl+C. Property test for byte-0x03 → CTRL_C_EVENT. |
| B-7 | Terminal renderer | WebView2 + xterm.js + WebGL addon. SharedBufferRequested data path. Asciicast v2 record/replay. |
| B-8 | Copilot driver | `Avelia.Agent.Copilot` — `GitHub.Copilot.SDK` 1.0.0-beta.4 wrapped to `IAgentSession`. Headless + Interactive modes. Reuses auth from B-3. |
| B-9 | Claude sidecar — runtime bundle | Vendor Node 20.x into `assets/runtime/node/`. Build-script that strips npm/corepack and verifies SHA. Installer integration. |
| B-10 | Claude sidecar — script + driver | `assets/agents/claude-host/claude-host.mjs` + `Avelia.Agent.ClaudeCode.ClaudeAgentSession`. JSON-RPC stdio. Headless + Interactive modes. |
| B-11 | Persistence | SQLite via Microsoft.Data.Sqlite. Schema: repositories, workspaces, conversations, messages, runs, settings. Hydrate on startup. Source-of-truth discipline. |
| B-12 | Composition + onboarding | Wire real services into `Composition.fs`. First-run UI: GitHub sign-in, agent auth (Anthropic key or Copilot inheritance), repo selection. Settings → Agents fully wired. |
| B-13 | End-to-end smoke | Real Claude headless run against a real repo, ending in a real PR. Real Copilot too. Document the prerequisite environment in README. |

Chunks B-0 through B-7 can ship without any agent driver — the shell already runs against stubs, so this is "real plumbing under the hood" without changing the UX surface. B-8+ light up the agentic features.

## Open questions / risks

1. **GitHub.Copilot.SDK 1.0.0-beta.4 stability.** GitHub iterated 30+ patch releases through 0.1.x. We pin and absorb churn; if the beta cadence stays this aggressive we may need to vendor the SDK source as a backup.
2. **Anthropic SDK billing transition (June 15, 2026).** Subscription users move to a separate "Agent SDK credit" pool. We surface usage in the UI clearly so users aren't surprised; cost telemetry comes from `--output-format json` final result, not the stream.
3. **Node bundle size + auto-update.** ~50 MB Node + ~80 MB Claude Code native bundle pushes the installer beyond comfortable for a desktop app. Worth a follow-up plan on "fetch Node + Claude binary on first run" vs ship-in-MSIX. Default for v1: ship-in-MSIX, accept the cost.
4. **Strategic: GitHub Copilot app (announced 2026-05-14).** GitHub's own desktop client with parallel sessions + per-session worktrees overlaps with Avelia directly. Watch what surface they expose (IPC? extension API?) but doesn't change v1 plans.
5. **Concurrent terminal + headless on the same session.** Both CLIs use the same on-disk session files; the SDK and a TUI hitting the same `.jsonl` simultaneously is undefined. v1 enforces mutual exclusion per session in `IAgentSessionFactory`. Future: investigate if the SDKs gain a coordination protocol.
6. **Worktree + index.lock races between Avelia and the user's terminal.** Don't auto-delete stale locks (has destroyed in-flight commits); surface a "stale lock" prompt instead.
7. **Defender scan slowdown** on `%USERPROFILE%\source\repos`. Offer a Settings → Performance button that adds the worktree root to Defender exclusions (via elevated PowerShell). Don't do it silently.

## Out of scope for v1

- Cloud Copilot coding agent (issue-assignment → PR). Future "remote execution" mode.
- macOS / Linux backends. The `ITerminalSession` and `ICredentialStore` abstractions are sized for this but no impls yet.
- Plugin runtime per `plugin-protocol.md`.
- Webhook relay for true push events (vs polling).
- Visual replay of asciicast files outside the live terminal panel.
