namespace Avelia.Core.Abstractions

open System
open System.Collections.Generic

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

/// How hard the agent reasons ("thinking mode") before answering. Mirrors the
/// thinking levels Copilot surfaces in VS Code — <c>Off</c> / <c>High</c> /
/// <c>Extra High</c> / <c>Max</c>; <see cref="ApiValue"/> is the exact wire token
/// the SDK expects. Surfaced in the composer's model bar and Settings → Agents.
type ReasoningEffort =
    | Off
    | Low
    | Medium
    | High
    | ExtraHigh
    | Max

    /// Wire token the Copilot SDK's <c>SessionConfig.ReasoningEffort</c> takes.
    /// These mirror the Copilot model catalog's <c>SupportedReasoningEfforts</c>
    /// vocabulary (<c>none/low/medium/high/xhigh/max</c>).
    member this.ApiValue: string =
        match this with
        | Off -> "none"
        | Low -> "low"
        | Medium -> "medium"
        | High -> "high"
        | ExtraHigh -> "xhigh"
        | Max -> "max"

    /// Title-case label for the picker.
    member this.Label: string =
        match this with
        | Off -> "Off"
        | Low -> "Low"
        | Medium -> "Medium"
        | High -> "High"
        | ExtraHigh -> "Extra High"
        | Max -> "Max"

    /// Visitor over the union — the C# binding point. Same pattern as
    /// <c>ModelChoice.Match</c>.
    member this.Match<'TResult>
        (
            off: System.Func<'TResult>,
            low: System.Func<'TResult>,
            medium: System.Func<'TResult>,
            high: System.Func<'TResult>,
            extraHigh: System.Func<'TResult>,
            max: System.Func<'TResult>
        ) : 'TResult =
        match this with
        | Off -> off.Invoke()
        | Low -> low.Invoke()
        | Medium -> medium.Invoke()
        | High -> high.Invoke()
        | ExtraHigh -> extraHigh.Invoke()
        | Max -> max.Invoke()

    /// All efforts in display order (lowest → highest).
    static member All: ReasoningEffort array = [| Off; Low; Medium; High; ExtraHigh; Max |]

    /// Parse a wire token — an SDK <c>SupportedReasoningEfforts</c> entry or a
    /// persisted value — into a <see cref="ReasoningEffort"/>. Accepts the current
    /// Copilot vocabulary (<c>none/low/medium/high/xhigh/max</c>) and the legacy
    /// tokens persisted before the vocabulary was aligned (<c>off</c>,
    /// <c>extra_high</c>). Returns <c>null</c> for an unrecognised token so callers
    /// (e.g. the model bar's per-model filter) can skip it.
    static member FromApiValue(token: string) : ReasoningEffort =
        match (if isNull token then "" else token.Trim().ToLowerInvariant()) with
        | "none"
        | "off" -> Off
        | "low" -> Low
        | "medium" -> Medium
        | "high" -> High
        | "xhigh"
        | "extra_high" -> ExtraHigh
        | "max" -> Max
        | _ -> Unchecked.defaultof<ReasoningEffort>

/// Context-window tier the agent runs with. Mirrors the Copilot SDK's
/// <c>ContextTier</c> (<c>default</c> / <c>long_context</c>); <see cref="ApiValue"/>
/// is the exact wire token. Surfaced in the composer's model bar and Settings → Agents.
type ContextTier =
    | Default
    | LongContext

    /// Wire token the Copilot SDK's <c>SessionConfig.ContextTier</c> takes.
    member this.ApiValue: string =
        match this with
        | Default -> "default"
        | LongContext -> "long_context"

    /// Label for the picker.
    member this.Label: string =
        match this with
        | Default -> "Default"
        | LongContext -> "Long context"

    /// Visitor over the union — the C# binding point.
    member this.Match<'TResult>(onDefault: System.Func<'TResult>, onLongContext: System.Func<'TResult>) : 'TResult =
        match this with
        | Default -> onDefault.Invoke()
        | LongContext -> onLongContext.Invoke()

    /// Both tiers in display order.
    static member All: ContextTier array = [| Default; LongContext |]

/// Backend-neutral description of one model the agent can run, as surfaced by
/// <c>IModelCatalogService</c>. Lets the shell's model picker list the live
/// Copilot catalog instead of a hardcoded set; the <c>Id</c> maps back to a
/// <c>ModelChoice</c> via <c>ModelCatalog.choiceOfId</c>.
type ModelInfo =
    {
        /// Catalog id (e.g. <c>"claude-sonnet-4.5"</c>).
        Id: string
        /// Human-readable label for the picker row.
        DisplayName: string
        /// One-line capability blurb. Empty when the backend supplies none.
        Description: string
        /// Reasoning-effort levels this model supports (e.g. <c>"low"</c>,
        /// <c>"medium"</c>, <c>"high"</c>). Empty when the model has no selectable
        /// thinking level. The composer offers these as "thinking mode" options.
        ReasoningEfforts: IReadOnlyList<string>
    }

/// Canonical model-id ⇄ <c>ModelChoice</c> mapping plus the built-in presets
/// the shell falls back to when the live catalog is unavailable (offline or
/// signed-out). The ids are the stable public catalog ids Copilot hosts the
/// Claude models under; <c>Avelia.Agent.Copilot.ModelMapping</c> defers to
/// <c>idOfChoice</c> so the outbound (session-config) and inbound (picker)
/// mappings can't drift apart.
[<RequireQualifiedAccess>]
module ModelCatalog =

    [<Literal>]
    let SonnetId = "claude-sonnet-4.5"

    [<Literal>]
    let OpusId = "claude-opus-4.1"

    [<Literal>]
    let HaikuId = "claude-haiku-4.5"

    /// Context-tier ids understood by the agent backend. <c>""</c> means "use
    /// the model's default tier"; <c>LongContext</c> is the extended (≈1M-token)
    /// window. These mirror the Copilot SDK's <c>ContextTier</c> string values.
    [<Literal>]
    let ContextDefault = "default"

    [<Literal>]
    let ContextLong = "long_context"

    /// Reasoning-effort levels offered for the built-in presets when the live
    /// catalog is unavailable. The live catalog reports each model's actual
    /// supported set; this is the offline fallback. Mirrors the vocabulary the
    /// Copilot catalog reports for the Claude presets (low / medium / high / xhigh).
    let defaultReasoningEfforts: IReadOnlyList<string> =
        [| "low"; "medium"; "high"; "xhigh" |] :> IReadOnlyList<_>

    /// <c>ModelChoice</c> → catalog id. <c>CustomModel</c> passes through; a
    /// blank custom name yields <c>""</c> so the SDK picks its own default.
    [<CompiledName("IdOfChoice")>]
    let idOfChoice (m: ModelChoice) : string =
        match m with
        | Sonnet45 -> SonnetId
        | Opus41 -> OpusId
        | Haiku45 -> HaikuId
        | CustomModel name -> if String.IsNullOrWhiteSpace name then "" else name

    /// Catalog id → <c>ModelChoice</c>. Ids beyond the three presets become
    /// <c>CustomModel id</c> so any live model the user picks round-trips
    /// through the persisted setting.
    [<CompiledName("ChoiceOfId")>]
    let choiceOfId (id: string) : ModelChoice =
        if String.IsNullOrWhiteSpace id then
            CustomModel ""
        elif String.Equals(id, SonnetId, StringComparison.OrdinalIgnoreCase) then
            Sonnet45
        elif String.Equals(id, OpusId, StringComparison.OrdinalIgnoreCase) then
            Opus41
        elif String.Equals(id, HaikuId, StringComparison.OrdinalIgnoreCase) then
            Haiku45
        else
            CustomModel id

    /// The three built-in presets: the stub catalog and the real catalog's
    /// offline fallback. Descriptions mirror the shell's prior hardcoded copy.
    [<CompiledName("Presets")>]
    let presets: ModelInfo array =
        [| { Id = SonnetId
             DisplayName = "Sonnet 4.5"
             Description = "Balanced — fastest default for most agent runs."
             ReasoningEfforts = defaultReasoningEfforts }
           { Id = OpusId
             DisplayName = "Opus 4.1"
             Description = "Most capable — pick this for tricky refactors and long contexts."
             ReasoningEfforts = defaultReasoningEfforts }
           { Id = HaikuId
             DisplayName = "Haiku 4.5"
             Description = "Lightweight — quickest token-throughput, smallest answers."
             ReasoningEfforts = defaultReasoningEfforts } |]

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
        /// Agent reasoning-effort / "thinking mode" id (e.g. <c>"high"</c>).
        /// Empty means "use the model's default". Maps to the Copilot SDK's
        /// <c>ReasoningEffort</c>.
        ReasoningEffort: string
        /// Agent context-window tier (<c>ModelCatalog.ContextDefault</c> /
        /// <c>ContextLong</c>). Empty means "use the model's default". Maps to
        /// the Copilot SDK's <c>ContextTier</c>.
        ContextTier: string
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
    /// Renames the conversation's display <c>Title</c> without appending a
    /// transcript message. Folds into <c>Conversation.Title</c> only; the
    /// branch/worktree name is unaffected. Emitted by the Haiku auto-rename
    /// once a task's first assistant reply lands.
    | TitleChanged of title: string

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
            onMarkdown: System.Func<AgentMarkdown, 'TResult>,
            onTitleChanged: System.Func<string, 'TResult>
        ) : 'TResult =
        match this with
        | UserMessageAppended u -> onUser.Invoke u
        | AgentMessageAppended a -> onAgent.Invoke a
        | AgentErrorAppended e -> onError.Invoke e
        | ToolBatchAppended t -> onTool.Invoke t
        | ChangeNoteAppended c -> onChange.Invoke c
        | AgentMarkdownAppended m -> onMarkdown.Invoke m
        | TitleChanged t -> onTitleChanged.Invoke t

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
