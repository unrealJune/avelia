namespace Avelia.Core.Abstractions

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

// ----------------------------------------------------------------------------
//  Persistence stores — the boundary *behind* the shell-facing services
//
//  Distinct from the `IxxxService` contracts in Services.fs: those are the
//  orchestration boundary the shell consumes; these are the durable-state
//  boundary the services sit on top of. In-memory implementations back them
//  today (`Avelia.Persistence.InMemoryStores`); SQLite implementations (B-11)
//  slot in behind the same interfaces with a single swap in composition.
// ----------------------------------------------------------------------------

/// Persistence DTO for a workspace. A superset of the shell-facing
/// <c>Workspace</c> record: it additionally carries the on-disk worktree root
/// (which <c>Workspace</c> deliberately omits — that record is bound directly
/// in XAML/DesignData and adding a path field would ripple through the shell)
/// and the 1:1 conversation binding. The store deals in <c>WorkspaceRecord</c>;
/// <c>IWorkspaceService</c> projects out <c>.Workspace</c> for the shell with
/// zero binding churn; the agent orchestrator reads <c>.WorktreePath</c> to
/// pin the agent's working directory.
type WorkspaceRecord =
    { Workspace: Workspace
      WorktreePath: RepoPath
      ConversationId: ConversationId }

/// Durable store of tracked repositories.
type IRepositoryStore =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<Repository>>
    abstract GetAsync: id: RepositoryId * CancellationToken -> Task<OperationResult<Repository>>
    abstract UpsertAsync: repo: Repository * CancellationToken -> Task<OperationResult<unit>>
    abstract RemoveAsync: id: RepositoryId * CancellationToken -> Task<OperationResult<unit>>

/// Durable store of workspaces (worktree + branch + conversation binding).
type IWorkspaceStore =
    abstract ListAllAsync: CancellationToken -> Task<IReadOnlyList<WorkspaceRecord>>
    abstract ListByRepoAsync: repoId: RepositoryId * CancellationToken -> Task<IReadOnlyList<WorkspaceRecord>>
    abstract GetAsync: id: WorkspaceId * CancellationToken -> Task<OperationResult<WorkspaceRecord>>
    abstract UpsertAsync: record: WorkspaceRecord * CancellationToken -> Task<OperationResult<unit>>
    abstract RemoveAsync: id: WorkspaceId * CancellationToken -> Task<OperationResult<unit>>

/// Durable store of event-sourced conversations.
type IConversationStore =
    abstract GetAsync: id: ConversationId * CancellationToken -> Task<OperationResult<Conversation>>

    /// The conversation bound to a workspace (1:1). <c>NotFound</c> until the
    /// workspace's conversation has been created.
    abstract GetByWorkspaceAsync: workspaceId: WorkspaceId * CancellationToken -> Task<OperationResult<Conversation>>

    abstract CreateAsync: conversation: Conversation * CancellationToken -> Task<OperationResult<unit>>

    /// Append a single message event and return the post-fold conversation, so
    /// the caller can broadcast a snapshot consistent with what was persisted.
    /// <c>NotFound</c> if the conversation id is unknown.
    abstract AppendEventAsync:
        id: ConversationId * event: MessageEvent * CancellationToken -> Task<OperationResult<Conversation>>

/// Durable store of the single appearance-settings record.
type ISettingsStore =
    abstract LoadAsync: CancellationToken -> Task<AppearanceSettings>
    abstract SaveAsync: settings: AppearanceSettings * CancellationToken -> Task<OperationResult<unit>>
