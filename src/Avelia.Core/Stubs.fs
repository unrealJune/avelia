namespace Avelia.Core.Stubs

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Avelia.Core
open Avelia.Core.Abstractions

// ============================================================================
//  Shared helpers
// ============================================================================

[<AutoOpen>]
module private Helpers =
    let inline asReadOnly (xs: seq<'T>) : IReadOnlyList<'T> = xs |> Seq.toArray :> IReadOnlyList<_>

    let inline notFound (label: string) = Failure(AveliaError.NotFound label)

// ============================================================================
//  Stub: Repository
// ============================================================================

type StubRepositoryService(initial: seq<Repository>) =
    let store = Dictionary<RepositoryId, Repository>()

    do
        for r in initial do
            store.[r.Id] <- r

    interface IRepositoryService with
        member _.ListAsync(ct: CancellationToken) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(asReadOnly store.Values)

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            match store.TryGetValue id with
            | true, repo -> Task.FromResult(Success repo)
            | _ -> Task.FromResult(notFound $"Repository {id}")

        member _.AddAsync(path, defaultBase, ct) =
            ct.ThrowIfCancellationRequested()
            let id = RepositoryId.create ()

            let name =
                let p = path.Value.Replace('\\', '/').TrimEnd('/')
                let lastSlash = p.LastIndexOf '/'
                if lastSlash < 0 then p else p.Substring(lastSlash + 1)

            let repo =
                { Id = id
                  Name = name
                  Path = path
                  DefaultBase = defaultBase
                  IsOpen = true }

            store.[id] <- repo
            Task.FromResult(Success repo)

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            if store.Remove id then
                Task.FromResult(Success())
            else
                Task.FromResult(notFound $"Repository {id}")

// ============================================================================
//  Stub: Workspace
// ============================================================================

type StubWorkspaceService(initial: seq<Workspace>) =
    let store = Dictionary<WorkspaceId, Workspace>()

    do
        for w in initial do
            store.[w.Id] <- w

    interface IWorkspaceService with
        member _.ListAllAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(asReadOnly store.Values)

        member _.ListByRepoAsync(repoId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(store.Values |> Seq.filter (fun w -> w.RepoId = repoId) |> asReadOnly)

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            match store.TryGetValue id with
            | true, w -> Task.FromResult(Success w)
            | _ -> Task.FromResult(notFound $"Workspace {id}")

        member _.CreateAsync(repoId, branch, baseBranch, ct) =
            ct.ThrowIfCancellationRequested()
            let id = WorkspaceId.create ()

            let ws: Workspace =
                { Id = id
                  RepoId = repoId
                  Branch = branch
                  Base = baseBranch
                  Status = WorkspaceStatus.Draft
                  DiffAdd = 0
                  DiffDel = 0
                  Agent = Sonnet45
                  LastUpdated = DateTimeOffset.UtcNow
                  LastUpdatedDisplay = "just now"
                  PrNumber = 0 }

            store.[id] <- ws
            Task.FromResult(Success ws)

        member _.ArchiveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            match store.TryGetValue id with
            | true, w ->
                if Workspace.canTransition w.Status WorkspaceStatus.Archived then
                    store.[id] <-
                        { w with
                            Status = WorkspaceStatus.Archived }

                    Task.FromResult(Success())
                else
                    Task.FromResult(Failure(AveliaError.Conflict $"Cannot archive from {w.Status}"))
            | _ -> Task.FromResult(notFound $"Workspace {id}")

// ============================================================================
//  Stub: Conversation
// ============================================================================

type StubConversationService
    (initialConversations: seq<Conversation>, workspaceLookup: WorkspaceId -> Conversation option) =
    let byId = Dictionary<ConversationId, Conversation>()

    do
        for c in initialConversations do
            byId.[c.Id] <- c

    // One channel per active observer. <c>PostUserMessageAsync</c> fans new
    // events out to every subscriber's channel; subscribers remove themselves
    // when their cancellation token is signalled. The lock guards mutation of
    // <c>subscribers</c> across post and subscribe operations.
    let subscribers =
        Dictionary<ConversationId, ResizeArray<Channel<ConversationUpdate>>>()

    let gate = obj ()

    let broadcast (conversationId: ConversationId) (update: ConversationUpdate) =
        let snapshot: Channel<ConversationUpdate> array =
            lock gate (fun () ->
                match subscribers.TryGetValue conversationId with
                | true, list -> list.ToArray()
                | _ -> Array.empty)

        for ch in snapshot do
            ch.Writer.TryWrite update |> ignore

    interface IConversationService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()

            match workspaceLookup workspaceId with
            | Some conv -> Task.FromResult(Success conv)
            | None -> Task.FromResult(notFound $"Conversation for workspace {workspaceId}")

        member _.PostUserMessageAsync(conversationId, text, refs, ct) =
            ct.ThrowIfCancellationRequested()

            match byId.TryGetValue conversationId with
            | true, conv ->
                let msg =
                    { Id = MessageId.create ()
                      Text = text
                      Refs = refs
                      Timestamp = DateTimeOffset.UtcNow }

                let event = UserMessageAppended msg
                byId.[conversationId] <- Conversation.applyEvent conv event
                broadcast conversationId (MessageAppended event)
                Task.FromResult(Success msg)
            | _ -> Task.FromResult(notFound $"Conversation {conversationId}")

        member _.ObserveMessages(conversationId, ct) =
            // AllowSynchronousContinuations lets the reader's continuation run on
            // the writer's thread when a value lands — keeps stub-driven flows
            // observable end-to-end on a single thread, which the shell's
            // ImmediateUiDispatcher tests depend on. A real backend (Chunk 10)
            // should leave this off so a slow VM can't stall the writer.
            let opts =
                UnboundedChannelOptions(SingleReader = true, AllowSynchronousContinuations = true)

            let channel = Channel.CreateUnbounded<ConversationUpdate>(opts)

            lock gate (fun () ->
                let list =
                    match subscribers.TryGetValue conversationId with
                    | true, l -> l
                    | _ ->
                        let l = ResizeArray<Channel<ConversationUpdate>>()
                        subscribers.[conversationId] <- l
                        l

                list.Add channel)

            let cleanup () =
                channel.Writer.TryComplete() |> ignore

                lock gate (fun () ->
                    match subscribers.TryGetValue conversationId with
                    | true, l -> l.Remove channel |> ignore
                    | _ -> ())

            let registration = ct.Register(Action(fun () -> cleanup ()))
            // Dispose the registration once the channel completes for any
            // reason (cancellation OR external Complete) — otherwise the
            // callback pins the channel + subscribers list for the lifetime
            // of the CT, even after the subscriber is gone.
            channel.Reader.Completion.ContinueWith(
                (fun _ -> registration.Dispose()),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore

            channel.Reader.ReadAllAsync(ct)

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
            Task.FromResult(workspaceFiles workspaceId)

        member _.GetPullRequestDiffAsync(prId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(prFiles prId)

        member _.GetHunksAsync(prId, file, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(prHunks (prId, file))

// ============================================================================
//  Stub: Pull request
// ============================================================================

type StubPullRequestService
    (prsByWorkspace: WorkspaceId -> PullRequest option, prsById: Dictionary<PullRequestId, PullRequest>) =
    interface IPullRequestService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()

            match prsByWorkspace workspaceId with
            | Some pr -> Task.FromResult(Success pr)
            | None -> Task.FromResult(notFound $"PullRequest for workspace {workspaceId}")

        member _.MergeAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            match prsById.TryGetValue id with
            | true, pr when pr.MergeReady ->
                prsById.[id] <-
                    { pr with
                        Status = PrStatus.Merged
                        MergeReady = false }

                Task.FromResult(Success())
            | true, pr -> Task.FromResult(Failure(AveliaError.Conflict $"PR #{pr.Number} not merge-ready"))
            | _ -> Task.FromResult(notFound $"PullRequest {id}")

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

// ============================================================================
//  Stub: Agent session factory
//
//  Fills the AveliaServices.Agents slot on the stub path. The stub conversation
//  service drives DesignData directly, so these sessions are never actually
//  pumped; they exist so buildStubServices compiles and any incidental caller
//  gets a benign no-op rather than a crash.
// ============================================================================

type private StubHeadlessSession() =
    interface IAgentSession with
        member _.SessionId = SessionId.create ()
        member _.Workspace = RepoPath.Create "C:/stub"
        member _.InterruptAsync(_ct) = Task.CompletedTask

        member _.WaitForExitAsync(_ct) =
            Task.FromResult { ExitCode = 0; IsClean = true }

    interface IHeadlessAgentSession with
        member _.Events(_ct) =
            // An immediately-completed stream (no events).
            let ch = Channel.CreateBounded<AgentEvent>(1)
            ch.Writer.TryComplete() |> ignore
            ch.Reader.ReadAllAsync _ct

        member _.SendUserMessageAsync(_text, _refs, _ct) = Task.FromResult(Success())
        member _.RespondToPermissionAsync(_id, _decision, _ct) = Task.FromResult(Success())

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask.CompletedTask

type StubAgentSessionFactory() =
    interface IAgentSessionFactory with
        member _.StartHeadlessAsync(_config, _ct) =
            Task.FromResult(Success(StubHeadlessSession() :> IHeadlessAgentSession))

        member _.StartInteractiveAsync(_config, _ct) =
            Task.FromResult(Failure(AveliaError.Internal "Interactive sessions require the real backend."))

type StubTerminalLaunchService() =
    interface ITerminalLaunchService with
        member _.StartAsync(_workspaceId, _ct) =
            Task.FromResult(Failure(AveliaError.Internal "The terminal requires the real backend (AVELIA_REAL=1)."))

// ============================================================================
//  Stub: Appearance / settings
//
//  Holds a single AppearanceSettings record in memory. Each setter clones the
//  record with the new field and returns Task.CompletedTask. Real persistence
//  (Chunk 10) will swap this for a SQLite-backed implementation; the shell
//  binding doesn't need to change.
// ============================================================================

type StubSettingsService(initial: AppearanceSettings) =
    let gate = obj ()
    let mutable current = initial
    let mutable hasToken = false

    interface ISettingsService with
        member _.GetAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult current

        member _.SetAccentAsync(accent, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> current <- { current with Accent = accent })
            Task.CompletedTask

        member _.SetDensityAsync(density, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> current <- { current with Density = density })
            Task.CompletedTask

        member _.SetTransparencyAsync(enabled, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> current <- { current with Transparency = enabled })
            Task.CompletedTask

        member _.SetOpenWithRightPanelAsync(enabled, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                current <-
                    { current with
                        OpenWithRightPanel = enabled })

            Task.CompletedTask

        member _.SetDefaultModelAsync(model, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> current <- { current with DefaultModel = model })
            Task.CompletedTask

        member _.SetExtendedThinkingAsync(enabled, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                current <-
                    { current with
                        ExtendedThinking = enabled })

            Task.CompletedTask

        member _.SetGitHubTokenAsync(token, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> hasToken <- not (System.String.IsNullOrWhiteSpace token))
            Task.FromResult(Success())

        member _.HasGitHubTokenAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(lock gate (fun () -> hasToken))
