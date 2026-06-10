module Avelia.Persistence.Tests.InMemoryStoresTests

open System
open System.Threading
open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence

let private ct = CancellationToken.None

let private repo (name: string) =
    { Id = RepositoryId.create ()
      Name = name
      Path = RepoPath.Create("C:/repos/" + name)
      DefaultBase = BranchName.Create "main"
      IsOpen = true }

let private workspaceRecord (repoId: RepositoryId) =
    let wsId = WorkspaceId.create ()

    let ws: Workspace =
        { Id = wsId
          RepoId = repoId
          Branch = BranchName.Create "feature/x"
          Base = BranchName.Create "main"
          Status = WorkspaceStatus.Draft
          DiffAdd = 0
          DiffDel = 0
          Agent = Sonnet45
          LastUpdated = DateTimeOffset.UnixEpoch
          LastUpdatedDisplay = "now"
          PrNumber = 0
          ReasoningEffort = ""
          ContextTier = "" }

    { Workspace = ws
      WorktreePath = RepoPath.Create("C:/worktrees/" + string (WorkspaceId.value wsId))
      ConversationId = ConversationId.create () }

let private userEvent (text: string) : MessageEvent =
    UserMessageAppended
        { Id = MessageId.create ()
          Text = text
          Refs = [||]
          Timestamp = DateTimeOffset.UnixEpoch }

// ---------------------------------------------------------------------------
//  Repository store
// ---------------------------------------------------------------------------

[<Fact>]
let ``repository upsert then get round-trips`` () =
    let store = InMemoryRepositoryStore() :> IRepositoryStore
    let r = repo "alpha"
    (store.UpsertAsync(r, ct)).Result |> ignore
    Assert.Equal(Success r, (store.GetAsync(r.Id, ct)).Result)

[<Fact>]
let ``repository get missing is NotFound`` () =
    let store = InMemoryRepositoryStore() :> IRepositoryStore

    match (store.GetAsync(RepositoryId.create (), ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``repository remove is idempotent`` () =
    let store = InMemoryRepositoryStore() :> IRepositoryStore
    let r = repo "beta"
    (store.UpsertAsync(r, ct)).Result |> ignore
    Assert.Equal(Success(), (store.RemoveAsync(r.Id, ct)).Result)
    Assert.Equal(Success(), (store.RemoveAsync(r.Id, ct)).Result) // gone already, still ok
    Assert.Empty((store.ListAsync ct).Result)

// ---------------------------------------------------------------------------
//  Workspace store
// ---------------------------------------------------------------------------

[<Fact>]
let ``workspace list-by-repo filters to the owning repo`` () =
    let store = InMemoryWorkspaceStore() :> IWorkspaceStore
    let repoA = RepositoryId.create ()
    let repoB = RepositoryId.create ()
    let a1 = workspaceRecord repoA
    let a2 = workspaceRecord repoA
    let b1 = workspaceRecord repoB

    for w in [ a1; a2; b1 ] do
        (store.UpsertAsync(w, ct)).Result |> ignore

    let forA = (store.ListByRepoAsync(repoA, ct)).Result
    Assert.Equal(2, forA.Count)
    Assert.All(forA, fun r -> Assert.Equal(repoA, r.Workspace.RepoId))

[<Fact>]
let ``workspace upsert overwrites by id`` () =
    let store = InMemoryWorkspaceStore() :> IWorkspaceStore
    let w = workspaceRecord (RepositoryId.create ())
    (store.UpsertAsync(w, ct)).Result |> ignore

    let updated =
        { w with
            Workspace =
                { w.Workspace with
                    Status = WorkspaceStatus.Active } }

    (store.UpsertAsync(updated, ct)).Result |> ignore
    Assert.Equal(1, (store.ListAllAsync ct).Result.Count)
    Assert.Equal(WorkspaceStatus.Active, (store.GetAsync(w.Workspace.Id, ct)).Result.Value.Workspace.Status)

// ---------------------------------------------------------------------------
//  Conversation store (event-sourced)
// ---------------------------------------------------------------------------

[<Fact>]
let ``conversation create then get-by-workspace finds it`` () =
    let store = InMemoryConversationStore() :> IConversationStore
    let wsId = WorkspaceId.create ()
    let conv = Conversation.empty (ConversationId.create ()) wsId "Title"
    (store.CreateAsync(conv, ct)).Result |> ignore
    Assert.Equal(Success conv, (store.GetByWorkspaceAsync(wsId, ct)).Result)

[<Fact>]
let ``appending to a missing conversation is NotFound`` () =
    let store = InMemoryConversationStore() :> IConversationStore

    match (store.AppendEventAsync(ConversationId.create (), userEvent "hi", ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

[<Property>]
let ``appending events folds the same as Conversation.replay`` (texts: NonEmptyArray<NonNull<string>>) =
    let store = InMemoryConversationStore() :> IConversationStore
    let convId = ConversationId.create ()
    let wsId = WorkspaceId.create ()
    (store.CreateAsync(Conversation.empty convId wsId "T", ct)).Result |> ignore

    let events = texts.Get |> Array.map (fun s -> userEvent s.Get)

    let mutable last = Conversation.empty convId wsId "T"

    for e in events do
        last <- (store.AppendEventAsync(convId, e, ct)).Result.Value

    let expected = Conversation.replay convId wsId "T" events
    // Same message count and sequence; event identity is preserved in order.
    last.LastSequence = expected.LastSequence
    && last.Messages.Length = expected.Messages.Length

// ---------------------------------------------------------------------------
//  Settings store
// ---------------------------------------------------------------------------

[<Fact>]
let ``settings save then load round-trips`` () =
    let initial = DesignData.defaultAppearance
    let store = InMemorySettingsStore(initial) :> ISettingsStore
    Assert.Equal(initial, (store.LoadAsync ct).Result)

    let changed =
        { initial with
            Density = Density.Compact }

    (store.SaveAsync(changed, ct)).Result |> ignore
    Assert.Equal(changed, (store.LoadAsync ct).Result)
