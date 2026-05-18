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

    /// Visitor over the union — keeps C# off the F# DU's nested case types.
    /// Same pattern as <c>MessageEvent.Match</c> / <c>OperationResult.Match</c>.
    member this.Match<'TResult>
        (
            sonnet45: System.Func<'TResult>,
            opus41: System.Func<'TResult>,
            haiku45: System.Func<'TResult>,
            custom: System.Func<string, 'TResult>
        ) : 'TResult =
        match this with
        | Sonnet45 -> sonnet45.Invoke()
        | Opus41 -> opus41.Invoke()
        | Haiku45 -> haiku45.Invoke()
        | CustomModel name -> custom.Invoke name

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

    /// Visitor over the union — keeps C# off the F# DU's nested case types
    /// and forces exhaustive handling at the call site. Same pattern as
    /// <c>MessageEvent.Match</c> / <c>ModelChoice.Match</c>: adding a new
    /// case here forces every C# consumer (e.g. the inbox row template
    /// selector) to handle it instead of silently falling through.
    member this.Match<'TResult>
        (onWarning: System.Func<'TResult>, onSuccess: System.Func<'TResult>, onInfo: System.Func<'TResult>)
        : 'TResult =
        match this with
        | Warning -> onWarning.Invoke()
        | Success -> onSuccess.Invoke()
        | Info -> onInfo.Invoke()

/// UI density preset. Maps to padding / row-height multipliers in the shell.
/// Mirrors the design's segmented control in Settings → Appearance.
[<RequireQualifiedAccess>]
type Density =
    | Compact
    | Comfortable

/// One of the six accent colors the user can pick in Settings → Appearance.
/// Each carries its CSS-style hex (the shell turns this into a brush). New
/// accents are additive — bumping this DU forces every consumer (theme service,
/// swatch picker) to handle the new case.
[<RequireQualifiedAccess>]
type AccentChoice =
    | SkyBlue
    | Violet
    | Magenta
    | Yellow
    | Orange
    | Sage

    /// Default CSS-style hex for the accent in dark mode. The shell keeps a
    /// Light-mode variant in its theme dictionary; the accent picker mutates
    /// the runtime ThemeResource value, so both palettes stay in sync.
    member this.Hex: string =
        match this with
        | SkyBlue -> "#4CC2FF"
        | Violet -> "#A78BFA"
        | Magenta -> "#F472B6"
        | Yellow -> "#FACC15"
        | Orange -> "#FB923C"
        | Sage -> "#6CCB5F"

    /// Visitor over the union — the C# binding point. Same pattern as
    /// <c>OperationResult.Match</c> / <c>ModelChoice.Match</c> so C# never
    /// touches the F# DU internals.
    member this.Match<'TResult>
        (
            skyBlue: System.Func<'TResult>,
            violet: System.Func<'TResult>,
            magenta: System.Func<'TResult>,
            yellow: System.Func<'TResult>,
            orange: System.Func<'TResult>,
            sage: System.Func<'TResult>
        ) : 'TResult =
        match this with
        | SkyBlue -> skyBlue.Invoke()
        | Violet -> violet.Invoke()
        | Magenta -> magenta.Invoke()
        | Yellow -> yellow.Invoke()
        | Orange -> orange.Invoke()
        | Sage -> sage.Invoke()

    /// All six accents in display order (matches the swatch row in the
    /// Appearance subpage). Exposed as a static member so C# bindings don't
    /// have to deal with F#'s module-suffix naming.
    static member All: AccentChoice array =
        [| AccentChoice.SkyBlue
           AccentChoice.Violet
           AccentChoice.Magenta
           AccentChoice.Yellow
           AccentChoice.Orange
           AccentChoice.Sage |]

/// Kind of file change in a diff.
type DiffKind =
    | Modified
    | Added
    | Deleted
    | Renamed of from: RelativePath

    /// Visitor over the union — keeps C# off the F# DU's nested case types
    /// and makes adding a new kind a compile error at every consumer.
    /// Mirrors <c>MessageEvent.Match</c> / <c>ModelChoice.Match</c>.
    member this.Match<'TResult>
        (
            onModified: System.Func<'TResult>,
            onAdded: System.Func<'TResult>,
            onDeleted: System.Func<'TResult>,
            onRenamed: System.Func<RelativePath, 'TResult>
        ) : 'TResult =
        match this with
        | Modified -> onModified.Invoke()
        | Added -> onAdded.Invoke()
        | Deleted -> onDeleted.Invoke()
        | Renamed from' -> onRenamed.Invoke from'

/// Per-line kind in a unified diff.
type DiffLineKind =
    | Context
    | Addition
    | Deletion

// ============================================================================
//  Records — repository / workspace / models
// ============================================================================

type Repository =
    { Id: RepositoryId
      Name: string
      Path: RepoPath
      DefaultBase: BranchName
      IsOpen: bool }

type Workspace =
    {
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

type UserMessage =
    {
        Id: MessageId
        Text: string
        /// Code-refs the user @-mentioned (file names without the leading @).
        Refs: string array
        Timestamp: DateTimeOffset
    }

type AgentMessage =
    { Id: MessageId
      Text: string
      Timestamp: DateTimeOffset }

type ToolBatch =
    {
        Id: MessageId
        ToolCount: int
        MessageCount: int
        /// Icon hints (e.g. <c>"files"</c>, <c>"search"</c>, <c>"terminal"</c>) so
        /// the renderer can show the inline icon strip from the design.
        ToolKinds: string array
        Timestamp: DateTimeOffset
    }

type ChangeNote =
    { Id: MessageId
      File: RelativePath
      Add: int
      Del: int
      Timestamp: DateTimeOffset }

type AgentMarkdownItem = { Bold: string; Detail: string }

type AgentMarkdown =
    {
        Id: MessageId
        /// Empty string if absent — keeps the shape C#-friendly (no Option boxing).
        Heading: string
        Body: string
        Items: AgentMarkdownItem array
        Timestamp: DateTimeOffset
    }

type AgentErrorMessage =
    { Id: MessageId
      Text: string
      Timestamp: DateTimeOffset }

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

    /// Visitor over the union — the C#-side projection point. Mirrors the
    /// pattern used by <c>OperationResult.Match</c>: typed delegates per case
    /// and the F# compiler enforces exhaustiveness, so adding a new event
    /// kind breaks compilation until every consumer is updated.
    member this.Match<'TResult>
        (
            onUser: System.Func<UserMessage, 'TResult>,
            onAgent: System.Func<AgentMessage, 'TResult>,
            onError: System.Func<AgentErrorMessage, 'TResult>,
            onTool: System.Func<ToolBatch, 'TResult>,
            onChange: System.Func<ChangeNote, 'TResult>,
            onMarkdown: System.Func<AgentMarkdown, 'TResult>
        ) : 'TResult =
        match this with
        | UserMessageAppended u -> onUser.Invoke u
        | AgentMessageAppended a -> onAgent.Invoke a
        | AgentErrorAppended e -> onError.Invoke e
        | ToolBatchAppended t -> onTool.Invoke t
        | ChangeNoteAppended c -> onChange.Invoke c
        | AgentMarkdownAppended m -> onMarkdown.Invoke m

type Conversation =
    { Id: ConversationId
      WorkspaceId: WorkspaceId
      Title: string
      Messages: MessageEvent array
      LastSequence: int }

// ============================================================================
//  Records — diffs
// ============================================================================

type DiffFile =
    {
        Path: RelativePath
        Add: int
        Del: int
        Kind: DiffKind
        /// The "active" file in the right-pane file list (the one whose diff is
        /// open in the diff viewer). Only one file per list is typically focused.
        IsFocused: bool
    }

type DiffLine =
    { LineNumber: int
      Kind: DiffLineKind
      Text: string }

type DiffHunk =
    {
        File: RelativePath
        /// Original hunk header from git (e.g. <c>"@@ -42,18 +42,28 @@"</c>).
        Header: string
        Lines: DiffLine array
    }

// ============================================================================
//  Records — pull request
// ============================================================================

type Check =
    {
        Name: string
        Status: CheckStatus
        Description: string
        /// Compact count label (e.g. <c>"24/24"</c>, <c>"82%"</c>, <c>"ok"</c>).
        Count: string
    }

type PullRequest =
    { Id: PullRequestId
      Number: int
      Title: string
      Branch: BranchName
      Base: BranchName
      Status: PrStatus
      Checks: Check array
      MergeReady: bool }

// ============================================================================
//  Records — inbox
// ============================================================================

type InboxItem =
    {
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
