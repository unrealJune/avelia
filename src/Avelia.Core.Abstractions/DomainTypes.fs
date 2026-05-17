namespace Avelia.Core.Abstractions

open System

// ============================================================================
//  Status DUs
// ============================================================================

/// State of a workspace (the branch + worktree the agent owns).
/// Mirrors the design's status-dot vocabulary at <c>data.jsx</c> + <c>styles-v2.css</c>.
[<RequireQualifiedAccess>]
type WorkspaceStatus =
    | Draft
    | Active
    | Ready
    | Conflict
    | Archived
    | Open

/// Which Claude model the workspace's agent is running.
type ModelChoice =
    | Sonnet45
    | Opus41
    | Haiku45
    | CustomModel of name: string

/// State of a pull request as Avelia tracks it.
[<RequireQualifiedAccess>]
type PrStatus =
    | Draft
    | Open
    | InReview
    | Approved
    | Merged
    | Closed

/// Outcome of a single CI check.
[<RequireQualifiedAccess>]
type CheckStatus =
    | Passed
    | Failed
    | Warn
    | Running
    | Skipped

/// User-visible inbox notification flavour.
[<RequireQualifiedAccess>]
type InboxItemKind =
    | Warning
    | Success
    | Info

/// Kind of file change in a diff.
type DiffKind =
    | Modified
    | Added
    | Deleted
    | Renamed of from: RelativePath

/// Per-line kind in a unified diff.
type DiffLineKind =
    | Context
    | Addition
    | Deletion

// ============================================================================
//  Records — repository / workspace / models
// ============================================================================

type Repository = {
    Id: RepositoryId
    Name: string
    Path: RepoPath
    DefaultBase: BranchName
    IsOpen: bool
}

type Workspace = {
    Id: WorkspaceId
    RepoId: RepositoryId
    Branch: BranchName
    Base: BranchName
    Status: WorkspaceStatus
    DiffAdd: int
    DiffDel: int
    Agent: ModelChoice
    LastUpdated: DateTimeOffset
    /// Pre-formatted relative-time string (e.g. <c>"12 min ago"</c>). The shell
    /// renders this verbatim so we don't push localization concerns into the VM.
    LastUpdatedDisplay: string
    /// Pull-request number associated with this workspace, or 0 if none.
    PrNumber: int
}

// ============================================================================
//  Records — conversation events
//
//  Every message kind in the design's transcript (data.jsx) has a payload
//  record here. The MessageEvent DU below unions them. New event kinds in the
//  future are additive — the C#-side DataTemplateSelector picks templates by
//  payload type, so no recompile of existing templates is needed.
// ============================================================================

type UserMessage = {
    Id: MessageId
    Text: string
    /// Code-refs the user @-mentioned (file names without the leading @).
    Refs: string array
    Timestamp: DateTimeOffset
}

type AgentMessage = {
    Id: MessageId
    Text: string
    Timestamp: DateTimeOffset
}

type ToolBatch = {
    Id: MessageId
    ToolCount: int
    MessageCount: int
    /// Icon hints (e.g. <c>"files"</c>, <c>"search"</c>, <c>"terminal"</c>) so
    /// the renderer can show the inline icon strip from the design.
    ToolKinds: string array
    Timestamp: DateTimeOffset
}

type ChangeNote = {
    Id: MessageId
    File: RelativePath
    Add: int
    Del: int
    Timestamp: DateTimeOffset
}

type AgentMarkdownItem = {
    Bold: string
    Detail: string
}

type AgentMarkdown = {
    Id: MessageId
    /// Empty string if absent — keeps the shape C#-friendly (no Option boxing).
    Heading: string
    Body: string
    Items: AgentMarkdownItem array
    Timestamp: DateTimeOffset
}

type AgentErrorMessage = {
    Id: MessageId
    Text: string
    Timestamp: DateTimeOffset
}

/// Event-sourced conversation primitive: appending an event yields a new
/// conversation whose <c>Messages</c> include the new entry and whose
/// <c>LastSequence</c> is one higher. Replay is just a left-fold over events.
type MessageEvent =
    | UserMessageAppended of UserMessage
    | AgentMessageAppended of AgentMessage
    | AgentErrorAppended of AgentErrorMessage
    | ToolBatchAppended of ToolBatch
    | ChangeNoteAppended of ChangeNote
    | AgentMarkdownAppended of AgentMarkdown

type Conversation = {
    Id: ConversationId
    WorkspaceId: WorkspaceId
    Title: string
    Messages: MessageEvent array
    LastSequence: int
}

// ============================================================================
//  Records — diffs
// ============================================================================

type DiffFile = {
    Path: RelativePath
    Add: int
    Del: int
    Kind: DiffKind
    /// The "active" file in the right-pane file list (the one whose diff is
    /// open in the diff viewer). Only one file per list is typically focused.
    IsFocused: bool
}

type DiffLine = {
    LineNumber: int
    Kind: DiffLineKind
    Text: string
}

type DiffHunk = {
    File: RelativePath
    /// Original hunk header from git (e.g. <c>"@@ -42,18 +42,28 @@"</c>).
    Header: string
    Lines: DiffLine array
}

// ============================================================================
//  Records — pull request
// ============================================================================

type Check = {
    Name: string
    Status: CheckStatus
    Description: string
    /// Compact count label (e.g. <c>"24/24"</c>, <c>"82%"</c>, <c>"ok"</c>).
    Count: string
}

type PullRequest = {
    Id: PullRequestId
    Number: int
    Title: string
    Branch: BranchName
    Base: BranchName
    Status: PrStatus
    Checks: Check array
    MergeReady: bool
}

// ============================================================================
//  Records — inbox
// ============================================================================

type InboxItem = {
    Id: Guid
    Title: string
    Description: string
    /// Pre-formatted age (e.g. <c>"4m"</c>, <c>"2mo"</c>). Like
    /// <c>Workspace.LastUpdatedDisplay</c>, the shell renders this verbatim.
    TimeAgo: string
    Kind: InboxItemKind
    /// Workspace this inbox item links to. <see cref="Guid.Empty"/> when unset.
    LinkedWorkspaceId: WorkspaceId
}
