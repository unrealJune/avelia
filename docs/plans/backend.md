# Avelia — Backend Implementation Plan

## Context

The Fluent shell (`winui-conductor-fluent.md`) ships against stub services in `Avelia.Core.Stubs`. That plan deferred "Chunk 10 — Real backend" as out of scope for v1. This plan covers Chunk 10 in detail: replacing the stubs with real agent drivers, local git operations, GitHub integration, terminal hosting, and persistence.

Today the agent and VCS projects exist as near-empty placeholders:

- `Avelia.Agent.ClaudeCode/ClaudeCode.fs` — one `AgentSettings` record.
- `Avelia.Vcs.GitHub/GitHub.fs` — one `RepoCoordinate` parser.

The typed service interfaces in `Avelia.Core.Abstractions/Services.fs` (`IRepositoryService`, `IWorkspaceService`, `IConversationService`, `IDiffService`, `IPullRequestService`, `IInboxService`, `ISettingsService`) already define the shell-facing contract. They are not yet enough for the backend — they describe **what the shell reads**, not **how the agent runs, how the terminal streams, or how git/GitHub plumbing is shaped**. This plan introduces those missing contracts.

## Decisions (locked in with user, 2026-05-18)

1. **Claude integration: bundled Node sidecar.** Avelia ships Node 20+ in the installer (~50 MB cost). A small sidecar script (`claude-host.mjs`, ~150 LoC) uses `@anthropic-ai/claude-agent-sdk` and exposes its `query()` async iterator over JSON-RPC stdio. F# core spawns one sidecar per session.
2. **Copilot integration: direct .NET SDK.** Take a NuGet dep on `GitHub.Copilot.SDK`. *(GitHub shipped GA between planning and B-8 — pinned to released **`1.0.0`**, not the `1.0.0-beta.4` originally sketched.)* The SDK manages its own JSON-RPC subprocess to the Copilot CLI server internally.
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

| Chunk | Status | Subject | Notes |
|------:|:--|:------|:------|
| B-0 | ✅ merged (#8) | Service-contract extension | Land `IAgentSession` + `IHeadlessAgentSession` + `IInteractiveAgentSession` + `IAgentSessionFactory`, `IGitOperations`, `IGitInspection`, `ITerminalSession`, `ICredentialStore`, `ISessionPersistence` + new primitives (`CommitId`, `CommitMessage`, `Remote`, `Worktree`, `WorktreeStatus`, `TerminalSize`, etc.) in `Avelia.Core.Abstractions`. Add `AveliaError.External` case. Delete legacy `ITaskService`/`IVcsService`/`IAgentService`. No impls yet — stubs continue to satisfy the shell. |
| B-1 | ✅ merged | Local git CLI driver | `Avelia.Vcs.Git.GitCli` — worktree add/remove/list, commit, push, fetch, checkout via `git.exe`. Per-repo `AsyncLock`. Long-paths check on startup. Integration tests against temp repos. |
| B-2 | ✅ merged | Local git inspection driver | `Avelia.Vcs.Git.GitInspector` — LibGit2Sharp 0.31 wrapper. Status, ahead/behind, log, branches, worktrees. Falls back to CLI on `unsupported repository version` (sparse, partial clone). |
| B-3 | ✅ merged (#10) | GitHub auth | Device Flow + GitHub App + PAT fallback. `Avelia.Vcs.GitHub.Auth`. Windows Credential Manager via `ICredentialStore`. Onboarding UI for first-run sign-in. |
| B-4 | ✅ merged (#10) | GitHub API client | Octokit.NET REST surface. ETag caching middleware. Rate-limit handling. Polling loops behind `IEventStream`. |
| B-5 | ✅ merged (#12) | GitHub dashboard query | Octokit.GraphQL.NET — PR list with checks + reviews. One batched query for the dashboard view. **Built via raw query string over `IConnection.Run`, not the LINQ builder — see progress log below.** |
| B-6 | 🔄 in review | ConPTY layer | `ConPtySession` P/Invoke (`Avelia.Shell.Windows/Terminal/`). Pseudo-console + overlapped named-pipe pairs + `STARTUPINFOEX`/`CreateProcessW`; resize, Ctrl+C (`0x03`), clean-vs-forced exit, single-consumer reads. Fast-tier interrupt-byte round-trip + Integration-tier ConPTY tests. **See progress log for the redirected-stdout test-host caveat.** |
| B-7 | 🔄 in review | Terminal renderer | Asciicast v2 record/replay (`Avelia.Persistence`, property-tested) + WebView2/xterm.js host (`TerminalView`) driven by a unit-tested `TerminalBridge` + `TerminalOutputBatcher`. **Output path is a base64 web message, not SharedArrayBuffer (yet) — see progress log.** |
| B-8 | 🔄 in review | Copilot driver | `Avelia.Agent.Copilot` — `GitHub.Copilot.SDK` **1.0.0 (GA)** wrapped to `IAgentSession`. Headless (SDK) + Interactive (ConPTY) modes. Reuses auth from B-3 via an injected `IGitHubTokenSource`. **Added `ITerminalSessionFactory` to Abstractions as the interactive-mode seam — see progress log.** |
| B-9 | ⬜ | Claude sidecar — runtime bundle | Vendor Node 20.x into `assets/runtime/node/`. Build-script that strips npm/corepack and verifies SHA. Installer integration. |
| B-10 | ⬜ | Claude sidecar — script + driver | `assets/agents/claude-host/claude-host.mjs` + `Avelia.Agent.ClaudeCode.ClaudeAgentSession`. JSON-RPC stdio. Headless + Interactive modes. |
| B-11 | 🔄 in review | Persistence | Store interfaces + `WorkspaceRecord` DTO + in-memory impls (with B-12). **SQLite impls now landed** (`Microsoft.Data.Sqlite`): `SqliteStores` (repositories / workspaces / conversations + messages / settings tables), event-sourced messages as JSON via a `Codec` module, shared in-memory↔SQLite contract suite, persist-across-reopen integration test. `RealComposition` reads `Storage.defaultDbPath()` — the `.db` file is the source of truth and persists across runs (startup hydration is automatic). |
| B-12 | 🔄 in review | Composition + onboarding | **Copilot-only e2e wired.** New `Avelia.Services` project: real `RepositoryService`/`WorkspaceService`/`SettingsService`/`AgentConversationService`/`InteractiveTerminalService` + `GitHubTokenSource` + `RealComposition.buildServices`. Shell cutover via `AVELIA_REAL=1`; ConPTY `ITerminalSessionFactory` in the shell. Minimal token onboarding (Settings → Agents PAT + env). New-workspace affordance. Interactive terminal panel hosting. See progress log. |
| B-13 | ⬜ | End-to-end smoke | Real Claude headless run against a real repo, ending in a real PR. Real Copilot too. Document the prerequisite environment in README. |
| B-14 | ✅ landed (local) | Self-hosting UX hardening | Round of fixes to make Avelia usable on its own development under `AVELIA_REAL=1`: live Copilot **model catalog** (`IModelCatalogService`), live **GitHub PR status** + CI checks (`PullRequestService` + `GitHubClientProvider`), **auto rail-name** worktrees + one-shot **Haiku title rename** (`MessageEvent.TitleChanged`), live **git status** surfacing + status-dot colors, **agent-config picker** (model + thinking-mode + context-tier, persisted), live **Edits/Changes** diff (`IGitInspection.DiffAsync` + `DiffService`), **delete-worktree**, merged tool-call transcript rows, send-button + terminal-launch (`.cmd` shim) fixes. Build clean, full fast suite green (~25 new tests). See progress log. |

Chunks B-0 through B-7 can ship without any agent driver — the shell already runs against stubs, so this is "real plumbing under the hood" without changing the UX surface. B-8+ light up the agentic features.

### Progress log — deviations & decisions found in-flight

- **B-14 (self-hosting UX hardening — six planned fixes + two rounds of live feedback; all build-clean and fast-tier-tested, WinUI runtime still unverified here).** A pass to make Avelia drive its own development under `AVELIA_REAL=1`. **Live model catalog:** `IModelCatalogService` + a `ModelCatalog` module (canonical id↔`ModelChoice` map, `presets`) in Abstractions; `CopilotModelCatalog` queries the SDK's `ListModelsAsync` and **falls back to the three presets on any failure** (offline / signed-out) so the picker is never empty; `ModelMapping.toCopilotModelId` now delegates to `ModelCatalog.idOfChoice` so the outbound (session-config) and inbound (picker) mappings can't drift. The Settings → Agents subpage and the composer both load it; Settings shows a loading ring. **Live GitHub PR status:** the stub PR service is replaced by `PullRequestService`, which derives the repo's `owner/name` from `git remote get-url origin` (new `IGitInspection.GetRemoteUrlAsync`, cached per repo), treats `PrNumber = 0` as the benign `NotFound` no-PR contract the pane already expected, and resolves the live PR via a lazy **`GitHubClientProvider`** (builds the Octokit client on first use from the first signed-in account, single-flighted under a `SemaphoreSlim`, **cached only on success** so signing in later works without a restart — the async-build-in-sync-composition problem). `ApiClient.GetPullRequestAsync` now also fetches check-runs for the PR head SHA and maps them through a **`ChecksMapping` helper lifted out of the GraphQL dashboard path** so REST and GraphQL classify checks identically; a checks failure degrades to an empty list rather than failing the PR. **Auto rail-names + Haiku rename:** a curated `WorktreeNames` pool (from `docs/worktree-names.txt`, embedded as a constant) with a collision-free `pickUnused`; `WorkspaceService.CreateAsync` treats an **empty `BranchName` sentinel** as "auto-name" (the shell's new-workspace flow dropped its prompt — `CreateWorkspaceAutoAsync`), so a workspace is born as e.g. `speedbird`. A new **`MessageEvent.TitleChanged`** case renames only the conversation's display `Title` — it folds in `Conversation.applyEvent` *without* appending a transcript row or bumping the sequence, round-trips through `Codec`, and is a **title-only `UPDATE`** in the SQLite `AppendEventAsync` (no message insert). `AgentConversationService` fires a **one-shot** background Haiku session (read-only, claimed via an interlocked flag) after the first assistant reply to summarize the task into a 3–6-word title. The shell's `MessageViewModel.FromEvent` gets a seventh `Match` arm that throws (TitleChanged is handled out-of-band in the VM's load + observe loops). **Agent-config picker (the "Opus 4.8 + MAX thinking + 1M context" ask):** `Workspace` and `AgentSessionConfig` gain `ReasoningEffort` + `ContextTier` strings (empty = model default); `CopilotConfig.build` maps them to the SDK's `ReasoningEffort` (string) and `ContextTier` (`"default"`/`"long_context"`); SQLite gets two new columns via an **idempotent `ALTER TABLE` migration** (best-effort, so existing `.db` files upgrade in place); `IWorkspaceService.SetAgentConfigAsync` persists the triple and **disposes the live session** so the next message restarts with the new config; the composer's decorative `ModelBadge` becomes a real `DropDownButton` flyout (Model / Thinking mode / Context window, the thinking levels driven by each model's live `SupportedReasoningEfforts`). **Live Edits/Changes:** new `IGitInspection.DiffAsync` (CLI-only: `diff --numstat` + `--name-status HEAD` merged, plus untracked via `ls-files --others`) → `DiffFile` list, served by a real `DiffService` (replaces the empty stub `IDiffService` in composition); `WorkspaceViewModel` schedules a **coalesced** diff refresh on agent activity so edits surface live. **Delete-worktree:** `IWorkspaceService.DeleteAsync` (dispose session → `git worktree remove --force` → best-effort branch delete → drop record) behind a right-click rail menu with a confirm dialog. **Transcript:** consecutive `ToolBatchViewModel`s merge into one collapsed strip (`AppendMessageVm`). **Two smaller fixes:** the composer `TextBox` got `UpdateSourceTrigger=PropertyChanged` so Send un-greys as you type (the bound `ComposerText` was only pushed on `LostFocus`); and the status dots stopped rendering black — `WorkspaceStatusToBrushConverter` was resolving Avelia palette brushes via the plain `Resources[key]` indexer, which **does not search `ThemeDictionaries`** (returns null → no fill), so it now walks `Resources.ThemeDictionaries[activeTheme]`; `MainViewModel.RefreshWorkspaceStatusesAsync` then drives the dots from live git (conflict → Conflict, dirty → Active, clean-and-ahead → Ready). **Terminal / interactive (real root cause):** the xterm bundle is vendored (`scripts/vendor-xterm.ps1`) with a new MSBuild guard that fails the build with an actionable message if it's missing; and "CreateProcess failed for: copilot" was **not** just a PATH-resolution gap — `CreateProcessW` only loads PE images, so it can't launch npm's `copilot.cmd` shim at all. `ConPtyTerminalSessionFactory.ResolveCommandLine` now resolves a bare command against `PATH`×`PATHEXT` **preferring an executable variant (`.exe`/`.cmd`) over the extensionless Unix shim**, and wraps a resolved `.cmd`/`.bat` in `%ComSpec% /s /c "…"` so the command processor runs it. **Test tiers:** ~25 new fast-tier tests (WorktreeNames properties, the `TitleChanged` fold + codec round-trip, auto-naming, `PullRequestService` against a scripted `IGitHubClient`, `DeleteAsync`/`SetAgentConfigAsync`, the `ReasoningEffort`/`ContextTier` config mapping, the diff-output parsers, composer-VM model/config paths); full fast suite green at 479. **Deferred / unchanged:** `SystemPromptAppend` still unmapped; PR diff + per-file hunks stay empty (only the workspace working-tree diff is live); merge-from-Avelia stays a stub-shaped `Conflict` (no `IGitHubClient` merge method yet); and — as with B-7/B-12 — the new WinUI surfaces (composer flyout, delete menu, status dots, live diff pane) are **build- and unit-verified only**; runtime behavior wants a manual `winapp run` under `AVELIA_REAL=1`.
- **B-11/B-12 (Copilot-only e2e: stubs → real backend, fully wired and tested at the fast tier; SQLite + live-WebView2-terminal are the two deferred tails).** Per a "defer B-10, integrate Copilot, get it running e2e" directive, the shell now runs a real backend behind `AVELIA_REAL=1` (default stays stub so design-time data + the E2E suite are untouched). **Persistence is interfaced now, in-memory today:** four store interfaces + a `WorkspaceRecord` DTO (superset of the shell-facing `Workspace`, carrying the worktree path the domain record deliberately omits) live in Abstractions; `Avelia.Persistence.InMemoryStores` backs them; SQLite (B-11) swaps in behind the same interfaces via one line in `RealComposition`. **New `Avelia.Services` project** (the composition root that can reference every driver without a cycle — `AveliaServices` stays in `Avelia.Core`, which references only Abstractions) houses the real services: `RepositoryService` (store + `IGitInspection` validate-on-add), `WorkspaceService` (adds `IWorkspaceService.CreateAsync` to the contract — there was no way to create a workspace before; materializes a real `git worktree` under `%LOCALAPPDATA%/Avelia/worktrees`), `SettingsService`, the keystone `AgentConversationService`, `InteractiveTerminalService`, and `GitHubTokenSource`. **The orchestrator** (`AgentConversationService : IConversationService`) lazily starts one headless Copilot session per workspace on the first message, pumps the single-consumer `session.Events` on a worker into the conversation's event stream (append-to-store then broadcast to `ObserveMessages` subscribers, reusing the stub's channel-broadcast shape), bridges the SDK permission callback to nothing (v1 runs `PermissionMode.AcceptEdits`), surfaces start/stream failures as `AgentErrorAppended` chat messages, and tears the session down on archive via a disposal delegate threaded through `WorkspaceService` (a delegate, not the service ref, to avoid a construction cycle). Races handled: double-start (per-conversation `SemaphoreSlim`), pump-only consumption of the single-consumer stream, pump-exception resets `Session=None` so the next post restarts; the subscribe-before-replay gap is documented and left for the SQLite phase's sequence-numbered replay. **Two contract seams added:** `ITerminalSessionFactory` (B-8) is implemented shell-side as a ConPTY wrapper the F# drivers can't reference directly, and `ITerminalLaunchService` resolves a workspace's worktree+model and starts an interactive session so the terminal panel needn't know either. **Auth is minimal-by-choice:** `GitHubTokenSource` resolves env (`COPILOT_GITHUB_TOKEN`→`GH_TOKEN`→`GITHUB_TOKEN`) → stored account → a PAT pasted into Settings → Agents (credential vault, never the settings record) → else `Unauthorized`; no device-flow onboarding UI yet. **Shell UI:** a "New workspace" rail item per repo (branch dialog → `CreateWorkspaceAsync` → worktree + conversation + open tab) and a live terminal panel that launches `copilot` in a ConPTY and bridges it to the B-7 `TerminalView`. **Test tiers:** 70+ new fast-tier tests cover the stores (round-trip + fold-equivalence properties), every service against fakes (incl. the orchestrator's lazy-start / pump-mapping / fan-out / isolation / teardown / error-surfacing), token-source precedence (property), and the new VM paths; an Integration-tier test materializes a real worktree, and a token-gated Integration test drives a real headless Copilot turn (no-ops without `COPILOT_GITHUB_TOKEN`). **Deferred tails:** SQLite stores (B-11), and the live `TerminalView`/WebView2 behavior is E2E-only (compile-verified here; runtime unverified, mirroring B-7's renderer split).
- **B-8 (Copilot SDK shipped GA — pinned `1.0.0`; SDK is callback-driven not poll-driven, so events bridge through a Channel; interactive mode needs a new terminal-factory seam; a few config/permission corners deferred).** The `GitHub.Copilot.SDK` released `1.0.0` between planning and implementation, so the dep is the GA build, not `1.0.0-beta.4`. The real flow is `new CopilotClient(CopilotClientOptions{ Mode=CopilotCli, GitHubToken, WorkingDirectory })` → `StartAsync` → `CreateSessionAsync(SessionConfig)`. Events are **push, not pull**: `SessionConfigBase.OnEvent : Action<SessionEvent>` fires for every event and `GetEventsAsync` only returns a *snapshot list* — so the driver wires `OnEvent` into an unbounded `Channel<AgentEvent>` and exposes `IHeadlessAgentSession.Events` as a single-consumer `taskSeq` over `Reader.ReadAllAsync`. The whole `SessionEvent`→`AgentEvent` projection is an eager, **stateless, fast-tier-unit-tested** module (`EventMapping.fs`); the two stateful signals it can't own — cumulative cost (a running total summed in the factory's event sink) and lifecycle (`Initialized` synthesized after `CreateSessionAsync`, `Ended` on dispose) — live in the session wrapper. Permissions bridge the SDK's *synchronous-return* `OnPermissionRequest` callback to the host's async approve/deny: `RequireApproval` parks the callback on a `TaskCompletionSource` keyed by a fresh id, emits `AgentEvent.PermissionRequired`, and `RespondToPermissionAsync` resolves it; `AcceptEdits`/`ReadOnly`/`Plan` answer inline without a round-trip. **New contract:** interactive mode hosts the raw `copilot` CLI in a ConPTY, but the F# driver can't reference the shell's ConPTY P/Invoke — so B-8 adds **`ITerminalSessionFactory`** to `Avelia.Core.Abstractions` (`StartAsync(commandLine, size, workingDirectory, ct) → OperationResult<ITerminalSession>`); the driver depends on that seam and the **shell-side ConPTY-backed implementation lands in B-12 composition** (same pattern as `ICredentialStore` — contract now, Windows impl at wiring time). Auth stays decoupled from `Avelia.Vcs.GitHub` (no project reference): the factory takes an `IGitHubTokenSource` that B-12 wires to B-3's stored token; an empty/failed token short-circuits to `Unauthorized` before any subprocess spawns. **Deferred corners (documented, not silently dropped):** `AgentSessionConfig.SystemPromptAppend` isn't mapped yet (the SDK's system-message override is a structured section/transform model deserving its own wiring); `PermissionDecision.AllowAlways` maps to the SDK's `ApproveOnce()` because GA `Rpc.PermissionDecision` only exposes `ApproveOnce/Reject/UserNotAvailable/NoResult` (no session-scoped approve); `ModelChoice`→Copilot model ids are best-effort against Copilot's catalog and reconciled at runtime in B-12. **Test tiers:** fast tier covers the pure mapping/config/permission logic *and* the SDK-free factory paths (token-failure short-circuit; the entire interactive path via a fake `ITerminalSessionFactory`/`ITerminalSession`) — 33 tests, no live process. A live headless turn and a live interactive terminal are **integration-tier** (need a real `copilot` install + a GitHub token) and aren't run in CI, mirroring B-6's host-gated ConPTY content check.
- **B-7 (asciicast codec is byte-exact via UTF-8 boundary buffering; renderer split into a testable bridge + an E2E-only WebView2 host; output path is base64 web-message, not SharedArrayBuffer — yet).** The record/replay core lives in `Avelia.Persistence` (`AsciiCast.fs`): an `IAsciiCastWriter` over any `Stream` and a `taskSeq`-based `replay` (the first F# `IAsyncEnumerable` producer — added the `FSharp.Control.TaskSeq` dep per the CLAUDE.md guidance). ConPTY can split a multibyte UTF-8 code point across reads, so the writer emits only the *complete-UTF-8 prefix* of its buffered bytes per event and carries the incomplete tail forward — every event's `data` stays valid UTF-8 (a genuine asciicast) and no byte is lost across the seam. That gives the required property `replay(serialize(stream)) = stream` for any valid-UTF-8 stream (the ConPTY domain), tested in-memory over `MemoryStream` so it runs in the *fast* tier; `SessionPersistence` (file-backed, `%LOCALAPPDATA%/Avelia/sessions/<id>.cast`, append-on-reopen) is covered by Integration-tier tests. The renderer is deliberately split: `TerminalOutputBatcher` (coalesce many ConPTY reads into one frame) and `TerminalBridge` (pump session↔renderer on an 8 ms timer, tee output to the recorder, forward input/resize) sit behind an `ITerminalRenderer` seam and are fully unit-tested with fakes; `TerminalView` (the WebView2 + xterm.js host) implements that seam and is E2E-only. **Deviation from decision 6 (SharedArrayBuffer):** the output path currently posts a base64 web message and the page `xterm.write()`s the decoded bytes — a correct, definitely-compiling baseline. The SharedArrayBuffer fast path is a contained follow-up that swaps only `TerminalView.WriteAsync` + the page's message handler, leaving the bridge/batcher untouched. xterm.js + the fit/WebGL addons are **vendored, not committed** (`scripts/vendor-xterm.ps1` → `Assets/terminal/vendor/`, packaged as Content) to keep the repo free of large bundles; run that script once after clone or the terminal panel is blank.
- **B-6 (ConPTY lives in the shell as WinUI-free P/Invoke; interrupt round-trip is a fast test, not a behavioural one; integration content assertion is host-capability-gated).** `ConPtySession` implements the F# `ITerminalSession` contract but is pure Win32 P/Invoke with zero `Microsoft.UI.Xaml` references, so — exactly like the platform-independent view-models — it is link-compiled into the (non-WinUI, `net10.0`) shell test assembly and exercised against real ConPTY on the Windows runner. Output/input use overlapped *named* pipes (`CreateNamedPipe` + `CreateFile`), not anonymous `CreatePipe`, because anonymous pipes can't be opened `FILE_FLAG_OVERLAPPED` and a non-overlapped `FileStream` can never honour a `CancellationToken` on a pending read (CLAUDE.md rule 6). `WaitForExitAsync` returns the shared `ProcessExit` record (the plan prose called it `TerminalExit`; the B-0 contract already named it `ProcessExit` and is reused by the agent-session wait calls too). The plan's "property test for byte-`0x03` → `CTRL_C_EVENT`" is realised as a *deterministic fast-tier* test over the `WriteInterruptAsync` seam (asserting the bytes emitted are exactly `0x03`) rather than a flaky behavioural assertion that a child trapped Ctrl+C — ConPTY owns the `0x03`→`CTRL_C_EVENT` translation, so the only thing our code controls is the byte. **Caveat found in-flight:** under a redirected-stdout test host (the agent harness, and likely `dotnet test` on CI) the ConPTY child's stdout is re-parented to the harness pipe and never rendered into the pseudo-console stream — only ConPTY's init frame comes through. Pseudo-console *attachment* still works (no child → 0 bytes; child → init frame + correct OSC title; resize/interrupt/clean-vs-forced-exit all verify), but the child-stdout **content** round-trip can't be observed there. The integration test probes this capability and selects the authoritative assertion accordingly (strong content check on an interactive console host; live-stream check on a headless host) so it neither false-fails nor silently no-ops. Run `./scripts/test-integration.ps1` from an interactive Windows console to exercise the full content round-trip.
- **B-5 (GraphQL via raw query string, not the LINQ builder).** The plan said "Octokit.GraphQL.NET", and B-5 takes the dependency — but it does *not* use the library's `Expression<Func<…>>` query builder. That builder translates C#-style member-init / anonymous-type expression trees, which F# cannot express (F# lambdas auto-quote to `System.Linq.Expressions`, but anonymous records and object-initialiser shapes don't translate). Instead the driver sends a hand-written GraphQL query string through the library's public `IConnection.Run(query, ct)` and parses the response envelope with `System.Text.Json`. `Octokit.GraphQL.Connection` still owns auth/endpoint/User-Agent. Upside: because we own the query text we own the response field names, so the whole `payload → Run → parse → map` path is unit-testable against hand-written JSON via a stub `IConnection` — no live endpoint, no integration-only coverage gap. The Octokit/REST and GraphQL surfaces sit behind one `IGitHubClient`; the GraphQL path lives behind an `IDashboardQuery` seam (`GraphQlDashboard.fs`) so `GitHubClient` stays constructable without a connection. GraphQL's separate points budget means `ListPrsForUserAsync` skips the REST rate-limit preflight. Pinned `Octokit.GraphQL` `0.4.0-beta` (latest published).

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
