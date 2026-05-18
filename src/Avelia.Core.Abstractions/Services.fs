namespace Avelia.Core.Abstractions

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

// ----------------------------------------------------------------------------
//  Design-driven service contracts
//
//  Each interface is the boundary the shell sees; the F# core, persistence
//  layer, and (future) GitHub / Claude Code adapters all sit behind these.
//  Snapshot methods only at this stage — live <c>Observe*</c> streams land
//  alongside the WorkspacePage in Chunk 3 (Channel-backed IAsyncEnumerable).
// ----------------------------------------------------------------------------

type IRepositoryService =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<Repository>>
    abstract GetAsync: id: RepositoryId * CancellationToken -> Task<OperationResult<Repository>>
    abstract AddAsync: path: RepoPath * defaultBase: BranchName * CancellationToken -> Task<OperationResult<Repository>>
    abstract RemoveAsync: id: RepositoryId * CancellationToken -> Task<OperationResult<unit>>

type IWorkspaceService =
    abstract ListAllAsync: CancellationToken -> Task<IReadOnlyList<Workspace>>
    abstract ListByRepoAsync: repoId: RepositoryId * CancellationToken -> Task<IReadOnlyList<Workspace>>
    abstract GetAsync: id: WorkspaceId * CancellationToken -> Task<OperationResult<Workspace>>
    abstract ArchiveAsync: id: WorkspaceId * CancellationToken -> Task<OperationResult<unit>>

type IConversationService =
    abstract GetForWorkspaceAsync: workspaceId: WorkspaceId * CancellationToken -> Task<OperationResult<Conversation>>

    abstract PostUserMessageAsync:
        conversationId: ConversationId * text: string * refs: string array * CancellationToken ->
            Task<OperationResult<UserMessage>>

    /// Stream of events appended to the conversation *after* the subscription
    /// starts. Pairs with <c>GetForWorkspaceAsync</c> for the initial snapshot.
    /// The enumerator completes when the cancellation token is signalled.
    ///
    /// <para>Threading contract: the shell consumes this on the UI thread (no
    /// <c>Task.Run</c> wrapping the iteration) so that synchronous channel
    /// continuations can fan events directly into the UI dispatcher without
    /// re-marshalling. Implementations therefore MUST NOT do blocking work on
    /// the call thread — both the initial subscription-setup and every
    /// <c>MoveNextAsync</c> must yield promptly. Real backends with sync I/O
    /// (database setup, file watcher registration) should hop to a worker
    /// thread internally and surface the stream as a non-blocking enumerable.</para>
    abstract ObserveMessages: conversationId: ConversationId * CancellationToken -> IAsyncEnumerable<MessageEvent>

type IDiffService =
    abstract GetWorkspaceDiffAsync: workspaceId: WorkspaceId * CancellationToken -> Task<IReadOnlyList<DiffFile>>
    abstract GetPullRequestDiffAsync: prId: PullRequestId * CancellationToken -> Task<IReadOnlyList<DiffFile>>

    abstract GetHunksAsync:
        prId: PullRequestId * file: RelativePath * CancellationToken -> Task<IReadOnlyList<DiffHunk>>

type IPullRequestService =
    abstract GetForWorkspaceAsync: workspaceId: WorkspaceId * CancellationToken -> Task<OperationResult<PullRequest>>
    abstract MergeAsync: id: PullRequestId * CancellationToken -> Task<OperationResult<unit>>

type IRunService =
    abstract ListAsync: workspaceId: WorkspaceId * CancellationToken -> Task<IReadOnlyList<RunId>>

type IInboxService =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<InboxItem>>

// ----------------------------------------------------------------------------
//  Appearance / settings
//
//  Theme proper lives shell-side (WinUI ElementTheme is platform state, not
//  domain state), but every other preference the design exposes — accent,
//  density, transparency, default model, open-with-right-panel — is plain data
//  and lives behind a typed service so future persistence (Chunk 10) can drop
//  in without touching the shell.
// ----------------------------------------------------------------------------

/// Snapshot of every appearance preference. Immutable; the shell rebuilds the
/// record on each setter call. Distinct from theme proper (Light/Dark/System)
/// which the shell-side <c>ThemeService</c> owns.
type AppearanceSettings =
    {
        Accent: AccentChoice
        Density: Density
        /// Mica / Acrylic backdrop on the main window. When <c>false</c> the shell
        /// falls back to the opaque surface brush.
        Transparency: bool
        /// When opening a workspace, also show the right pane (PR/Files/Terminal).
        /// Mirrors the design's Settings → Appearance toggle.
        OpenWithRightPanel: bool
        /// User's chosen default agent model. The composer can still override
        /// per-conversation; this is the initial selection.
        DefaultModel: ModelChoice
        /// Extended-thinking toggle in Settings → Agents.
        ExtendedThinking: bool
    }

type ISettingsService =
    /// Current snapshot. Cheap to call; the stub returns the in-memory record.
    abstract GetAsync: CancellationToken -> Task<AppearanceSettings>
    abstract SetAccentAsync: accent: AccentChoice * CancellationToken -> Task
    abstract SetDensityAsync: density: Density * CancellationToken -> Task
    abstract SetTransparencyAsync: enabled: bool * CancellationToken -> Task
    abstract SetOpenWithRightPanelAsync: enabled: bool * CancellationToken -> Task
    abstract SetDefaultModelAsync: model: ModelChoice * CancellationToken -> Task
    abstract SetExtendedThinkingAsync: enabled: bool * CancellationToken -> Task

// ----------------------------------------------------------------------------
//  Local git — operations + inspection
//
//  Split along mutating / read-only lines so the driver projects can pick
//  different implementations per surface (CLI for writes, libgit2 for reads).
//  All inspection methods are async even though LibGit2Sharp is sync internally
//  — gives us cancellation support and lets a future driver swap to true async
//  I/O without surface churn.
// ----------------------------------------------------------------------------

/// Mutating git operations. Implementations shell out to <c>git.exe</c> so the
/// user's signing config, hooks, and LFS filters apply unchanged. Concurrency
/// is serialized per repository (not per worktree) inside the implementation —
/// <c>.git/packed-refs</c> and the object DB are shared across worktrees and
/// two concurrent commits in different worktrees can race.
type IGitOperations =
    /// Create a new worktree at <paramref name="worktree"/> with a freshly
    /// branched <paramref name="branch"/> from the repo's current HEAD. Fails
    /// with <c>Conflict</c>-shaped error if the branch already exists; callers
    /// who need to attach an existing branch must use <c>CheckoutAsync</c>
    /// inside an existing worktree instead.
    abstract WorktreeAddAsync:
        repo: RepoPath * branch: BranchName * worktree: RepoPath * CancellationToken -> Task<OperationResult<Worktree>>

    abstract WorktreeRemoveAsync: worktree: RepoPath * force: bool * CancellationToken -> Task<OperationResult<unit>>

    abstract CommitAsync:
        worktree: RepoPath * message: CommitMessage * CancellationToken -> Task<OperationResult<CommitId>>

    abstract PushAsync: worktree: RepoPath * remote: Remote * CancellationToken -> Task<OperationResult<unit>>
    abstract FetchAsync: worktree: RepoPath * remote: Remote * CancellationToken -> Task<OperationResult<unit>>

    /// Switch the worktree to <paramref name="branch"/>. Fails (External
    /// error) if the worktree has uncommitted changes that would be
    /// overwritten by the switch. Callers must <c>CommitAsync</c> or stash
    /// before invoking; this method never discards user work.
    abstract CheckoutAsync: worktree: RepoPath * branch: BranchName * CancellationToken -> Task<OperationResult<unit>>

    abstract BranchCreateAsync:
        repo: RepoPath * branch: BranchName * baseRef: BranchName * CancellationToken -> Task<OperationResult<unit>>

    /// Delete a branch from the repo. When <paramref name="force"/> is false
    /// the call fails for unmerged branches (git's <c>-d</c> behavior); when
    /// true it deletes regardless (<c>-D</c>). The agent-workspace flow
    /// regularly archives unmerged work and wants the force path; routine
    /// cleanup should pass false.
    abstract BranchDeleteAsync:
        repo: RepoPath * branch: BranchName * force: bool * CancellationToken -> Task<OperationResult<unit>>

/// Read-only git inspection. Default impl uses LibGit2Sharp (cheap, no
/// subprocess churn for polling refresh paths). Falls back to <c>git.exe</c>
/// when LibGit2Sharp can't open the repo (sparse, partial clone, etc.).
///
/// <b>Cancellation caveat.</b> Implementations are async with a
/// <c>CancellationToken</c>, but the LibGit2 path is synchronous internally
/// and wrapped via <c>Task.Run</c> — the token cancels the <em>waiter</em>,
/// not the in-flight libgit2 walk. Cancellation is prompt on the CLI
/// fallback path (the subprocess is killed) and best-effort on the libgit2
/// path. Callers that must interrupt mid-walk on a large repo should not
/// rely on this surface.
type IGitInspection =
    abstract StatusAsync: worktree: RepoPath * CancellationToken -> Task<OperationResult<WorktreeStatus>>

    abstract LogAsync:
        worktree: RepoPath * limit: int * CancellationToken -> Task<OperationResult<IReadOnlyList<CommitInfo>>>

    /// Local branches only (refs under <c>refs/heads</c>). Remote-tracking
    /// branches are out of scope — fetch them via a future
    /// <c>ListRemoteBranchesAsync</c> if needed.
    abstract ListBranchesAsync: repo: RepoPath * CancellationToken -> Task<OperationResult<IReadOnlyList<BranchName>>>

    /// All worktrees attached to the repo, including the main checkout (the
    /// caller doesn't need to synthesize it separately).
    abstract ListWorktreesAsync: repo: RepoPath * CancellationToken -> Task<OperationResult<IReadOnlyList<Worktree>>>

// ----------------------------------------------------------------------------
//  Agent — per-session driver + factory
//
//  One base interface (<c>IAgentSession</c>) shared between modes, two
//  specializations (<c>IHeadlessAgentSession</c> for chat-driven, SDK-mediated
//  flows; <c>IInteractiveAgentSession</c> for terminal-hosted CLI flows), one
//  factory (<c>IAgentSessionFactory</c>) per agent kind (Claude, Copilot).
//
//  Lifecycle is owned by the factory — by the time you have an interface, the
//  session is running. Disposal terminates the process and flushes any
//  persisted session state.
// ----------------------------------------------------------------------------

/// Common lifecycle for any agent session, regardless of run mode.
type IAgentSession =
    inherit IAsyncDisposable
    abstract SessionId: SessionId
    abstract Workspace: RepoPath
    /// Send a soft interrupt (Ctrl+C equivalent) to the agent. The session
    /// stays alive; use <c>DisposeAsync</c> to terminate.
    abstract InterruptAsync: CancellationToken -> Task
    /// Resolves when the agent process exits. <c>ProcessExit.IsClean</c>
    /// distinguishes a self-exit from one forced by <c>InterruptAsync</c> or
    /// <c>DisposeAsync</c> — the shell renders these differently.
    abstract WaitForExitAsync: CancellationToken -> Task<ProcessExit>

/// Headless mode: SDK-driven, events streamed into the chat UI.
type IHeadlessAgentSession =
    inherit IAgentSession
    /// Live event stream. Completes when the session ends or the token is
    /// cancelled. Always emits <c>AgentEvent.Initialized</c> first and
    /// <c>AgentEvent.Ended</c> last.
    ///
    /// <b>Single-consumer.</b> The returned <c>IAsyncEnumerable</c> is hot —
    /// calling <c>GetAsyncEnumerator</c> more than once on the same session
    /// throws <see cref="System.InvalidOperationException"/>. The shell binds
    /// the stream once into its chat projection and shares the events via
    /// its own observable layer.
    abstract Events: CancellationToken -> IAsyncEnumerable<AgentEvent>

    /// Send a user message to the agent. <paramref name="refs"/> are
    /// opaque-to-this-layer reference strings (file paths, URLs, etc.) that
    /// each driver may interpret per its own SDK conventions; an empty array
    /// is the no-refs case (no nullability at the boundary).
    abstract SendUserMessageAsync: text: string * refs: string array * CancellationToken -> Task<OperationResult<unit>>

    /// Reply to a <c>PermissionRequired</c> event. The driver resumes the
    /// SDK stream once the decision lands. Responding to an unknown or
    /// already-resolved <paramref name="requestId"/> returns
    /// <c>Failure (NotFound "permission:&lt;guid&gt;")</c>.
    abstract RespondToPermissionAsync:
        requestId: Guid * decision: PermissionDecision * CancellationToken -> Task<OperationResult<unit>>

/// Interactive mode: CLI hosted in a ConPTY; bytes stream into the terminal
/// panel. The driver still owns lifecycle (<c>IAgentSession</c>); the terminal
/// is the user-facing surface.
type IInteractiveAgentSession =
    inherit IAgentSession
    abstract Terminal: ITerminalSession

/// Factory for sessions of a single agent kind. The shell selects via
/// configuration; one factory is registered per kind in Composition.
and IAgentSessionFactory =
    abstract StartHeadlessAsync:
        config: AgentSessionConfig * CancellationToken -> Task<OperationResult<IHeadlessAgentSession>>

    abstract StartInteractiveAsync:
        config: AgentSessionConfig * CancellationToken -> Task<OperationResult<IInteractiveAgentSession>>

// ----------------------------------------------------------------------------
//  Terminal — pseudo-terminal hosting a child process
//
//  Bytes in, bytes out. No knowledge of xterm.js, WebView2, or the renderer.
//  Windows impl uses ConPTY; future macOS/Linux backends can implement against
//  forkpty(3) without surface change.
// ----------------------------------------------------------------------------

and ITerminalSession =
    inherit IAsyncDisposable
    abstract Size: TerminalSize
    abstract WriteAsync: bytes: ReadOnlyMemory<byte> * CancellationToken -> Task
    /// Bytes from the child's stdout/stderr (combined). The enumerator
    /// completes when the child exits or the token is cancelled.
    ///
    /// <b>Single-consumer.</b> Calling <c>GetAsyncEnumerator</c> more than
    /// once on the same session throws
    /// <see cref="System.InvalidOperationException"/>. The shell's terminal
    /// view binds once and fans out via its asciicast/replay layer if it
    /// needs to multicast.
    abstract ReadAllAsync: CancellationToken -> IAsyncEnumerable<ReadOnlyMemory<byte>>
    abstract ResizeAsync: size: TerminalSize * CancellationToken -> Task
    /// Writes <c>0x03</c> to the input pipe; ConPTY converts to
    /// <c>CTRL_C_EVENT</c> for the child's process group. The Windows-impl
    /// property test asserts the round-trip.
    abstract SendInterruptAsync: CancellationToken -> Task
    abstract WaitForExitAsync: CancellationToken -> Task<ProcessExit>

// ----------------------------------------------------------------------------
//  Credential store — secret vault behind a tiny interface
//
//  Windows-first impl uses Credential Manager; future macOS Keychain / Linux
//  libsecret backends slot in without surface change. Keys are
//  application-scoped strings (e.g. <c>"avelia:github:&lt;login&gt;"</c>); the
//  vault does no parsing.
// ----------------------------------------------------------------------------

type ICredentialStore =
    /// Retrieve a stored secret by <paramref name="key"/>. A missing key
    /// surfaces as <c>Failure (AveliaError.NotFound "credential:&lt;key&gt;")</c>
    /// — distinct from "empty secret stored" (which returns
    /// <c>Success ""</c>; an empty string is a legal credential value, e.g.
    /// a placeholder during onboarding).
    abstract GetAsync: key: string * CancellationToken -> Task<OperationResult<string>>
    abstract SetAsync: key: string * secret: string * CancellationToken -> Task<OperationResult<unit>>
    /// Remove a stored secret. Deleting a missing key returns
    /// <c>Success ()</c> (idempotent), not <c>NotFound</c>.
    abstract DeleteAsync: key: string * CancellationToken -> Task<OperationResult<unit>>

// ----------------------------------------------------------------------------
//  Session persistence — asciicast v2 record/replay
//
//  One <c>.cast</c> file per terminal/agent session; append-only JSONL of
//  <c>[time, "o", bytes]</c> tuples. On session reopen the writer's previous
//  output is replayed into xterm.js as fast as possible to rebuild scrollback
//  before attaching the live ConPTY.
// ----------------------------------------------------------------------------

type IAsciiCastWriter =
    inherit IAsyncDisposable
    /// Append a chunk of terminal output. <c>elapsed</c> is measured from the
    /// session start (the writer captures the header timestamp on open).
    abstract AppendAsync: bytes: ReadOnlyMemory<byte> * elapsed: TimeSpan * CancellationToken -> Task

type ISessionPersistence =
    abstract OpenWriterAsync: sessionId: SessionId * CancellationToken -> Task<OperationResult<IAsciiCastWriter>>

    /// Open a replay stream for an existing session. The outer
    /// <c>OperationResult</c> resolves once: <c>Failure (NotFound "session:&lt;id&gt;")</c>
    /// when the session's <c>.cast</c> file doesn't exist; <c>Success</c>
    /// otherwise. The inner stream completes when the cast is fully replayed
    /// or the token is cancelled; iteration may raise on disk errors.
    /// Mirrors the <c>IAsyncEnumerable</c> shape of <c>Events</c> /
    /// <c>ReadAllAsync</c> for consistency.
    abstract OpenReplayAsync:
        sessionId: SessionId * CancellationToken ->
            Task<OperationResult<IAsyncEnumerable<ReadOnlyMemory<byte>>>>
