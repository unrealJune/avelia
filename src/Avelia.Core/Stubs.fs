namespace Avelia.Core.Stubs

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avelia.Core
open Avelia.Core.Abstractions

// ============================================================================
//  Shared helpers
// ============================================================================

[<AutoOpen>]
module private Helpers =
    let inline asReadOnly (xs: seq<'T>) : IReadOnlyList<'T> =
        xs |> Seq.toArray :> IReadOnlyList<_>

    let inline notFound (label: string) =
        Failure (AveliaError.NotFound label)

// ============================================================================
//  Stub: Repository
// ============================================================================

type StubRepositoryService(initial: seq<Repository>) =
    let store = Dictionary<RepositoryId, Repository>()
    do for r in initial do store.[r.Id] <- r

    interface IRepositoryService with
        member _.ListAsync(ct: CancellationToken) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(asReadOnly store.Values)

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            match store.TryGetValue id with
            | true, repo -> Task.FromResult (Success repo)
            | _ -> Task.FromResult (notFound $"Repository {id}")

        member _.AddAsync(path, defaultBase, ct) =
            ct.ThrowIfCancellationRequested()
            let id = RepositoryId.create ()
            let name =
                let p = path.Value.Replace('\\', '/').TrimEnd('/')
                let lastSlash = p.LastIndexOf '/'
                if lastSlash < 0 then p else p.Substring(lastSlash + 1)
            let repo = {
                Id = id
                Name = name
                Path = path
                DefaultBase = defaultBase
                IsOpen = true
            }
            store.[id] <- repo
            Task.FromResult (Success repo)

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            if store.Remove id then
                Task.FromResult (Success ())
            else
                Task.FromResult (notFound $"Repository {id}")

// ============================================================================
//  Stub: Workspace
// ============================================================================

type StubWorkspaceService(initial: seq<Workspace>) =
    let store = Dictionary<WorkspaceId, Workspace>()
    do for w in initial do store.[w.Id] <- w

    interface IWorkspaceService with
        member _.ListAllAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(asReadOnly store.Values)

        member _.ListByRepoAsync(repoId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(
                store.Values
                |> Seq.filter (fun w -> w.RepoId = repoId)
                |> asReadOnly)

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            match store.TryGetValue id with
            | true, w -> Task.FromResult (Success w)
            | _ -> Task.FromResult (notFound $"Workspace {id}")

        member _.ArchiveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            match store.TryGetValue id with
            | true, w ->
                if Workspace.canTransition w.Status WorkspaceStatus.Archived then
                    store.[id] <- { w with Status = WorkspaceStatus.Archived }
                    Task.FromResult (Success ())
                else
                    Task.FromResult (Failure (AveliaError.Conflict $"Cannot archive from {w.Status}"))
            | _ -> Task.FromResult (notFound $"Workspace {id}")

// ============================================================================
//  Stub: Conversation
// ============================================================================

type StubConversationService
    (
        initialConversations: seq<Conversation>,
        workspaceLookup: WorkspaceId -> Conversation option
    ) =
    let byId = Dictionary<ConversationId, Conversation>()
    do for c in initialConversations do byId.[c.Id] <- c

    interface IConversationService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()
            match workspaceLookup workspaceId with
            | Some conv -> Task.FromResult (Success conv)
            | None -> Task.FromResult (notFound $"Conversation for workspace {workspaceId}")

        member _.PostUserMessageAsync(conversationId, text, refs, ct) =
            ct.ThrowIfCancellationRequested()
            match byId.TryGetValue conversationId with
            | true, conv ->
                let msg = {
                    Id = MessageId.create ()
                    Text = text
                    Refs = refs
                    Timestamp = DateTimeOffset.UtcNow
                }
                let event = UserMessageAppended msg
                byId.[conversationId] <- Conversation.applyEvent conv event
                Task.FromResult (Success msg)
            | _ -> Task.FromResult (notFound $"Conversation {conversationId}")

// ============================================================================
//  Stub: Diff
// ============================================================================

type StubDiffService
    (
        workspaceFiles: WorkspaceId -> IReadOnlyList<DiffFile>,
        prFiles: PullRequestId -> IReadOnlyList<DiffFile>,
        prHunks: PullRequestId * RelativePath -> IReadOnlyList<DiffHunk>
    ) =
    interface IDiffService with
        member _.GetWorkspaceDiffAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult (workspaceFiles workspaceId)

        member _.GetPullRequestDiffAsync(prId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult (prFiles prId)

        member _.GetHunksAsync(prId, file, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult (prHunks (prId, file))

// ============================================================================
//  Stub: Pull request
// ============================================================================

type StubPullRequestService
    (
        prsByWorkspace: WorkspaceId -> PullRequest option,
        prsById: Dictionary<PullRequestId, PullRequest>
    ) =
    interface IPullRequestService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()
            match prsByWorkspace workspaceId with
            | Some pr -> Task.FromResult (Success pr)
            | None -> Task.FromResult (notFound $"PullRequest for workspace {workspaceId}")

        member _.MergeAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            match prsById.TryGetValue id with
            | true, pr when pr.MergeReady ->
                prsById.[id] <- { pr with Status = PrStatus.Merged; MergeReady = false }
                Task.FromResult (Success ())
            | true, pr ->
                Task.FromResult (Failure (AveliaError.Conflict $"PR #{pr.Number} not merge-ready"))
            | _ ->
                Task.FromResult (notFound $"PullRequest {id}")

// ============================================================================
//  Stub: Run
// ============================================================================

type StubRunService() =
    interface IRunService with
        member _.ListAsync(_workspaceId, ct) =
            ct.ThrowIfCancellationRequested()
            // No active runs in the stub. Real impl wires to processes/Docker.
            Task.FromResult(asReadOnly Seq.empty<RunId>)

// ============================================================================
//  Stub: Inbox
// ============================================================================

type StubInboxService(initial: seq<InboxItem>) =
    let store = ResizeArray initial

    interface IInboxService with
        member _.ListAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(asReadOnly store)
