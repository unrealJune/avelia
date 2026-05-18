namespace Avelia.Core.Abstractions

open System
open System.Collections.Generic

// ============================================================================
//  Git — worktree snapshot, working-tree status, log entries
// ============================================================================

type Worktree =
    {
        Path: RepoPath
        Branch: BranchName
        Head: CommitId
        /// Whether <c>git worktree</c> has the lock file in place — locked
        /// worktrees aren't removable without <c>--force</c>.
        IsLocked: bool
    }

/// Commit counts vs an upstream ref. Both fields default to zero when the
/// branch has no configured upstream — callers must decide whether to display.
type AheadBehind = { Ahead: int; Behind: int }

type WorkingTreeFileStatus =
    { Path: RelativePath
      IsModified: bool
      IsStaged: bool
      IsUntracked: bool
      IsConflicted: bool }

/// Single-call snapshot of a working tree — folds branch / ahead-behind /
/// file-list into one read so a polling shell doesn't pay four .git seeks
/// per refresh.
type WorktreeStatus =
    {
        Branch: BranchName
        AheadBehind: AheadBehind
        Files: WorkingTreeFileStatus array
        /// Derived: true when any <c>Files</c> entry is modified, staged,
        /// untracked, or conflicted. Exposed pre-computed so the shell's status
        /// dot doesn't reduce the array on every binding tick.
        HasUncommittedChanges: bool
    }

type CommitInfo =
    { Id: CommitId
      Author: string
      AuthoredAt: DateTimeOffset
      Subject: string }

// ============================================================================
//  Terminal — shape carried by ConPTY events and Resize calls
// ============================================================================

/// Terminal dimensions in character cells. <c>Cols</c> and <c>Rows</c> are
/// both positive; the type provides no enforcement (callers validate at the
/// boundary) because ConPTY ranges drift with Windows versions.
type TerminalSize = { Cols: int; Rows: int }

type TerminalExit =
    {
        ExitCode: int
        /// True when the child exited on its own; false if killed by
        /// <c>InterruptAsync</c>, process termination, or a host crash. The
        /// shell renders these differently — a clean exit is informational
        /// while a forced exit is a warning.
        IsClean: bool
    }

// ============================================================================
//  Agent — config, events, permission requests, cost snapshots
// ============================================================================

/// Tool-call gating policy for a headless session. Maps onto SDK-specific
/// settings at each driver's boundary.
[<RequireQualifiedAccess>]
type PermissionMode =
    /// Auto-approve file writes (SDK default for headless).
    | AcceptEdits
    /// Every tool call asks the host via <c>AgentEvent.PermissionRequired</c>.
    | RequireApproval
    /// Reject any mutation tool — read-only inspection mode.
    | ReadOnly
    /// Claude "plan" mode — read-only plus propose-but-don't-execute.
    | Plan

    member this.Match<'TResult>
        (
            acceptEdits: System.Func<'TResult>,
            requireApproval: System.Func<'TResult>,
            readOnly: System.Func<'TResult>,
            plan: System.Func<'TResult>
        ) : 'TResult =
        match this with
        | AcceptEdits -> acceptEdits.Invoke()
        | RequireApproval -> requireApproval.Invoke()
        | ReadOnly -> readOnly.Invoke()
        | Plan -> plan.Invoke()

/// Host's response to a <c>PermissionRequest</c>. <c>AllowAlways</c> upgrades
/// the permission for the rest of the session (per-tool, per-target where the
/// SDK supports it).
type PermissionDecision =
    | Allow
    | Deny
    | AllowAlways

    member this.Match<'TResult>
        (allow: System.Func<'TResult>, deny: System.Func<'TResult>, allowAlways: System.Func<'TResult>)
        : 'TResult =
        match this with
        | Allow -> allow.Invoke()
        | Deny -> deny.Invoke()
        | AllowAlways -> allowAlways.Invoke()

type PermissionRequest =
    {
        RequestId: Guid
        ToolName: string
        /// Raw JSON of the tool input as the SDK presented it — the shell
        /// formats this for the approval dialog. Schema is tool-specific so
        /// we don't decode at this layer.
        ToolInputJson: string
        /// Human-readable summary the SDK provided ("Edit src/foo.fs",
        /// "Run cargo test"). Empty when the SDK didn't supply one.
        Description: string
    }

/// Cumulative usage for a session. <c>CostMicroUsd</c> is stored as
/// integral microdollars (1e-6 USD) so the boundary never carries float —
/// gives six decimal places of precision and round-trips cleanly through
/// C#'s decimal binding.
type CostSnapshot =
    { InputTokens: int
      OutputTokens: int
      CostMicroUsd: int64 }

/// Configuration for an MCP server the agent should attach to. Mirrors the
/// shape the Claude / Copilot SDKs expect on session start.
type McpServerConfig =
    { Command: string
      Args: string array
      Env: IReadOnlyDictionary<string, string> }

/// Configuration handed to <c>IAgentSessionFactory</c> for either run mode.
/// Sentinels per the project convention — no <c>'T option</c> in fields the
/// shell touches: <c>SystemPromptAppend = ""</c> means "no append",
/// <c>AllowedTools = [||]</c> means "SDK default", <c>ResumeSessionId = ""</c>
/// means "new session".
type AgentSessionConfig =
    {
        /// Worktree root the agent runs against.
        Workspace: RepoPath
        Model: ModelChoice
        SystemPromptAppend: string
        AllowedTools: string array
        PermissionMode: PermissionMode
        McpServers: IReadOnlyDictionary<string, McpServerConfig>
        /// On-disk session id to resume. Empty for a new session.
        ResumeSessionId: string
    }

/// Events emitted by a headless session. <c>Conversation</c> wraps the
/// existing <c>MessageEvent</c> so chat-projection code is shared with the
/// stub-driven flow; the remaining cases are session-lifecycle signals the
/// chat view doesn't need to render.
[<RequireQualifiedAccess>]
type AgentEvent =
    /// First event of every session. <c>sessionId</c> is the on-disk id the
    /// driver allocated (use for <c>ResumeSessionId</c> later).
    | Initialized of sessionId: string * model: ModelChoice
    | Conversation of event: MessageEvent
    /// Mid-flight cost update. Best-effort: Copilot streams these; Claude
    /// only emits one at end-of-turn. Consumers must treat as advisory.
    | CostUpdated of snapshot: CostSnapshot
    /// Host must reply via <c>RespondToPermissionAsync</c>; the SDK stream
    /// is paused until a decision comes back.
    | PermissionRequired of request: PermissionRequest
    | RetryAttempt of attempt: int * delayMs: int * reason: string
    | Warning of message: string
    /// Terminal event. Always emitted; the events stream completes after.
    | Ended of exitCode: int * totals: CostSnapshot

    member this.Match<'TResult>
        (
            onInitialized: System.Func<string, ModelChoice, 'TResult>,
            onConversation: System.Func<MessageEvent, 'TResult>,
            onCost: System.Func<CostSnapshot, 'TResult>,
            onPermission: System.Func<PermissionRequest, 'TResult>,
            onRetry: System.Func<int, int, string, 'TResult>,
            onWarning: System.Func<string, 'TResult>,
            onEnded: System.Func<int, CostSnapshot, 'TResult>
        ) : 'TResult =
        match this with
        | Initialized(sid, model) -> onInitialized.Invoke(sid, model)
        | Conversation e -> onConversation.Invoke e
        | CostUpdated s -> onCost.Invoke s
        | PermissionRequired r -> onPermission.Invoke r
        | RetryAttempt(a, d, reason) -> onRetry.Invoke(a, d, reason)
        | Warning msg -> onWarning.Invoke msg
        | Ended(code, totals) -> onEnded.Invoke(code, totals)
