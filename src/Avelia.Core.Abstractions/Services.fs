namespace Avelia.Core.Abstractions

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

// ----------------------------------------------------------------------------
//  Legacy interfaces (predate the design — preserved while older callers
//  migrate. New code should target the typed services below.)
// ----------------------------------------------------------------------------

type ITaskService =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<TaskId>>

type IVcsService =
    abstract CurrentBranchAsync: CancellationToken -> Task<string>

type IAgentService =
    abstract StartSessionAsync: prompt: string * CancellationToken -> Task<SessionId>

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
        conversationId: ConversationId
        * text: string
        * refs: string array
        * CancellationToken
        -> Task<OperationResult<UserMessage>>
    /// Stream of events appended to the conversation *after* the subscription
    /// starts. Pairs with <c>GetForWorkspaceAsync</c> for the initial snapshot.
    /// The enumerator completes when the cancellation token is signalled.
    abstract ObserveMessages: conversationId: ConversationId * CancellationToken -> IAsyncEnumerable<MessageEvent>

type IDiffService =
    abstract GetWorkspaceDiffAsync: workspaceId: WorkspaceId * CancellationToken -> Task<IReadOnlyList<DiffFile>>
    abstract GetPullRequestDiffAsync: prId: PullRequestId * CancellationToken -> Task<IReadOnlyList<DiffFile>>
    abstract GetHunksAsync: prId: PullRequestId * file: RelativePath * CancellationToken -> Task<IReadOnlyList<DiffHunk>>

type IPullRequestService =
    abstract GetForWorkspaceAsync: workspaceId: WorkspaceId * CancellationToken -> Task<OperationResult<PullRequest>>
    abstract MergeAsync: id: PullRequestId * CancellationToken -> Task<OperationResult<unit>>

type IRunService =
    abstract ListAsync: workspaceId: WorkspaceId * CancellationToken -> Task<IReadOnlyList<RunId>>

type IInboxService =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<InboxItem>>
