module Avelia.Services.Tests.AgentConversationServiceTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services

let private ct = CancellationToken.None
let private epoch () = DateTimeOffset.UnixEpoch

/// Seed one workspace + bound conversation into fresh stores; return the ids.
let private seed (stores: Stores) =
    let repoId = RepositoryId.create ()
    let wsId = WorkspaceId.create ()
    let convId = ConversationId.create ()

    let ws: Workspace =
        { Id = wsId
          RepoId = repoId
          Branch = BranchName.Create "feature/x"
          Base = BranchName.Create "main"
          Status = WorkspaceStatus.Active
          DiffAdd = 0
          DiffDel = 0
          Agent = Sonnet45
          LastUpdated = DateTimeOffset.UnixEpoch
          LastUpdatedDisplay = "now"
          PrNumber = 0
          ReasoningEffort = ""
          ContextTier = "" }

    let record =
        { Workspace = ws
          WorktreePath = RepoPath.Create("C:/wt/" + string (WorkspaceId.value wsId))
          ConversationId = convId }

    (stores.Workspaces.UpsertAsync(record, ct)).Result |> ignore

    (stores.Conversations.CreateAsync(Conversation.empty convId wsId "t", ct)).Result
    |> ignore

    wsId, convId

let private waitUntil (cond: unit -> bool) (timeoutMs: int) =
    let sw = Stopwatch.StartNew()

    while not (cond ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        Thread.Sleep 5

    cond ()

/// Collect up to <paramref name="n"/> updates from the stream, giving up after
/// a timeout.
let private collect (n: int) (stream: IAsyncEnumerable<ConversationUpdate>) =
    task {
        let results = ResizeArray<ConversationUpdate>()
        use timeout = new CancellationTokenSource(2000)
        let e = stream.GetAsyncEnumerator timeout.Token

        try
            let mutable go = true

            while go && results.Count < n do
                let! moved = e.MoveNextAsync()
                if moved then results.Add e.Current else go <- false
        with _ ->
            ()

        return List.ofSeq results
    }

let private mk (factory: IAgentSessionFactory) (stores: Stores) =
    new AgentConversationService(factory, stores.Conversations, stores.Workspaces, stores.Settings, epoch)

[<Fact>]
let ``no session starts until the first message`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    use cts = new CancellationTokenSource()
    (svc :> IConversationService).ObserveMessages(convId, cts.Token) |> ignore
    Thread.Sleep 50
    Assert.Equal(0, factory.StartCount)

[<Fact>]
let ``posting broadcasts the user message and starts a session`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let collecting = collect 1 (isvc.ObserveMessages(convId, cts.Token))

    let posted = (isvc.PostUserMessageAsync(convId, "hello", [||], ct)).Result
    Assert.True posted.IsSuccess

    match collecting.Result with
    | [ MessageAppended(UserMessageAppended m) ] -> Assert.Equal("hello", m.Text)
    | other -> failwithf "unexpected %A" other

    Assert.True(waitUntil (fun () -> factory.StartCount = 1) 2000)
    Assert.True(waitUntil (fun () -> factory.Sessions.Count = 1 && factory.Sessions.[0].Sent.Contains "hello") 2000)

[<Fact>]
let ``a session TurnEnded becomes a non-persisted TurnCompleted update`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let collecting = collect 2 (isvc.ObserveMessages(convId, cts.Token))

    (isvc.PostUserMessageAsync(convId, "hi", [||], ct)).Result |> ignore
    Assert.True(waitUntil (fun () -> factory.Sessions.Count = 1) 2000)

    // The agent finishes the turn.
    factory.Sessions.[0].Emit AgentEvent.TurnEnded

    match collecting.Result with
    | [ MessageAppended(UserMessageAppended _); TurnCompleted ] -> ()
    | other -> failwithf "unexpected %A" other

    // TurnCompleted is ephemeral — only the user message is persisted.
    let conv = (stores.Conversations.GetAsync(convId, ct)).Result.Value
    Assert.Equal(1, conv.Messages.Length)

[<Fact>]
let ``agent conversation events are mapped to the stream and persisted`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let collecting = collect 2 (isvc.ObserveMessages(convId, cts.Token))

    (isvc.PostUserMessageAsync(convId, "hi", [||], ct)).Result |> ignore
    Assert.True(waitUntil (fun () -> factory.Sessions.Count = 1) 2000)

    let agentMsg =
        AgentMessageAppended
            { Id = MessageId.create ()
              Text = "working on it"
              Timestamp = DateTimeOffset.UnixEpoch }

    factory.Sessions.[0].Emit(AgentEvent.Conversation agentMsg)

    match collecting.Result with
    | [ MessageAppended(UserMessageAppended _); MessageAppended(AgentMessageAppended a) ] ->
        Assert.Equal("working on it", a.Text)
    | other -> failwithf "unexpected %A" other

    // Both events are persisted in the conversation.
    let conv = (stores.Conversations.GetAsync(convId, ct)).Result.Value
    Assert.Equal(2, conv.Messages.Length)

[<Fact>]
let ``multiple subscribers all receive the broadcast`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let svc = mk (FakeAgentSessionFactory()) stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let a = collect 1 (isvc.ObserveMessages(convId, cts.Token))
    let b = collect 1 (isvc.ObserveMessages(convId, cts.Token))

    (isvc.PostUserMessageAsync(convId, "fanout", [||], ct)).Result |> ignore

    let isUser =
        function
        | [ MessageAppended(UserMessageAppended m) ] -> m.Text = "fanout"
        | _ -> false

    Assert.True(isUser a.Result)
    Assert.True(isUser b.Result)

[<Fact>]
let ``a failed session start surfaces as an error message in chat`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory(AveliaError.Unauthorized)
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let collecting = collect 2 (isvc.ObserveMessages(convId, cts.Token))

    // The post itself still succeeds — the message was accepted.
    Assert.True((isvc.PostUserMessageAsync(convId, "go", [||], ct)).Result.IsSuccess)

    match collecting.Result with
    | [ MessageAppended(UserMessageAppended _); MessageAppended(AgentErrorAppended e) ] ->
        Assert.Contains("authorized", e.Text)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``two conversations get isolated sessions`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convA = seed stores
    let _, convB = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let aEvents = collect 1 (isvc.ObserveMessages(convA, cts.Token))

    (isvc.PostUserMessageAsync(convA, "a", [||], ct)).Result |> ignore
    (isvc.PostUserMessageAsync(convB, "b", [||], ct)).Result |> ignore

    Assert.True(waitUntil (fun () -> factory.StartCount = 2) 2000)
    // Distinct sessions, each sent only its own message.
    Assert.Equal(2, factory.Sessions.Count)
    Assert.True(waitUntil (fun () -> factory.Sessions |> Seq.forall (fun s -> s.Sent.Count = 1)) 2000)
    // A's subscriber only saw A's message.
    match aEvents.Result with
    | [ MessageAppended(UserMessageAppended m) ] -> Assert.Equal("a", m.Text)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``disposing a conversation tears down its session and completes subscribers`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let _, convId = seed stores
    let factory = FakeAgentSessionFactory()
    let svc = mk factory stores
    let isvc = svc :> IConversationService
    use cts = new CancellationTokenSource()
    let drained = collect 5 (isvc.ObserveMessages(convId, cts.Token))

    (isvc.PostUserMessageAsync(convId, "x", [||], ct)).Result |> ignore
    Assert.True(waitUntil (fun () -> factory.Sessions.Count = 1) 2000)

    (svc.DisposeConversationAsync convId).Wait()

    Assert.True(waitUntil (fun () -> factory.Sessions.[0].Disposed = 1) 2000)
    // The subscriber stream completed (collect returns once the channel closes).
    Assert.True(waitUntil (fun () -> drained.IsCompleted) 2000)
