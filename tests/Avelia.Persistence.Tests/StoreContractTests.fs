module Avelia.Persistence.Tests.StoreContractTests

open System
open System.IO
open System.Threading
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence

let private ct = CancellationToken.None

// -- fixtures ----------------------------------------------------------------

let private repo name =
    { Id = RepositoryId.create ()
      Name = name
      Path = RepoPath.Create("C:/repos/" + name)
      DefaultBase = BranchName.Create "main"
      IsOpen = true }

let private record (repoId: RepositoryId) (convId: ConversationId) =
    let wsId = WorkspaceId.create ()

    let ws: Workspace =
        { Id = wsId
          RepoId = repoId
          Branch = BranchName.Create "feature/x"
          Base = BranchName.Create "main"
          Status = WorkspaceStatus.Active
          DiffAdd = 4
          DiffDel = 1
          Agent = Opus41
          LastUpdated = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
          LastUpdatedDisplay = "yesterday"
          PrNumber = 7 }

    { Workspace = ws
      WorktreePath = RepoPath.Create("C:/wt/" + string (WorkspaceId.value wsId))
      ConversationId = convId }

let private userEvent text : MessageEvent =
    UserMessageAppended
        { Id = MessageId.create ()
          Text = text
          Refs = [||]
          Timestamp = DateTimeOffset.UnixEpoch }

// -- the contract: identical assertions for any Stores impl ------------------

let runContract (stores: Stores) =
    // Repositories
    let r = repo "widgets"
    (stores.Repositories.UpsertAsync(r, ct)).Result |> ignore
    Assert.Equal(Success r, (stores.Repositories.GetAsync(r.Id, ct)).Result)
    Assert.Single((stores.Repositories.ListAsync ct).Result) |> ignore

    match (stores.Repositories.GetAsync(RepositoryId.create (), ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "expected NotFound, got %A" other

    // Workspaces (round-trips the worktree-path binding the domain record omits)
    let convId = ConversationId.create ()
    let rec0 = record r.Id convId
    (stores.Workspaces.UpsertAsync(rec0, ct)).Result |> ignore
    let fetched = (stores.Workspaces.GetAsync(rec0.Workspace.Id, ct)).Result.Value
    Assert.Equal(rec0.WorktreePath, fetched.WorktreePath)
    Assert.Equal(rec0.Workspace.Agent, fetched.Workspace.Agent)
    Assert.Equal(rec0.Workspace.PrNumber, fetched.Workspace.PrNumber)
    Assert.Single((stores.Workspaces.ListByRepoAsync(r.Id, ct)).Result) |> ignore

    // Conversations (event-sourced)
    (stores.Conversations.CreateAsync(Conversation.empty convId rec0.Workspace.Id "T", ct)).Result |> ignore
    Assert.True((stores.Conversations.GetByWorkspaceAsync(rec0.Workspace.Id, ct)).Result.IsSuccess)
    let afterFirst = (stores.Conversations.AppendEventAsync(convId, userEvent "one", ct)).Result.Value
    Assert.Equal(1, afterFirst.LastSequence)
    let afterSecond = (stores.Conversations.AppendEventAsync(convId, userEvent "two", ct)).Result.Value
    Assert.Equal(2, afterSecond.LastSequence)
    Assert.Equal(2, (stores.Conversations.GetAsync(convId, ct)).Result.Value.Messages.Length)

    match (stores.Conversations.AppendEventAsync(ConversationId.create (), userEvent "x", ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "expected NotFound, got %A" other

    // Settings
    let changed = { DesignData.defaultAppearance with Density = Density.Compact; Accent = AccentChoice.Sage }
    (stores.Settings.SaveAsync(changed, ct)).Result |> ignore
    Assert.Equal(changed, (stores.Settings.LoadAsync ct).Result)

[<Fact>]
let ``in-memory stores satisfy the contract`` () =
    runContract (InMemoryStores.create DesignData.defaultAppearance)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``sqlite stores satisfy the contract`` () =
    let path = Path.Combine(Path.GetTempPath(), "avelia-sql-" + Guid.NewGuid().ToString("N") + ".db")
    let set = SqliteStores.create path DesignData.defaultAppearance

    try
        try
            runContract set.Stores
        finally
            (set :> IDisposable).Dispose()
    finally
        try File.Delete path with _ -> ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``sqlite persists across reopen`` () =
    let path = Path.Combine(Path.GetTempPath(), "avelia-sql-" + Guid.NewGuid().ToString("N") + ".db")

    try
        let repoId = RepositoryId.create ()
        let convId = ConversationId.create ()
        let rec0 = record repoId convId

        // Session 1: write a repo, workspace, conversation with two events.
        let s1 = SqliteStores.create path DesignData.defaultAppearance

        try
            (s1.Stores.Repositories.UpsertAsync(repo "persisted", ct)).Result |> ignore
            (s1.Stores.Workspaces.UpsertAsync(rec0, ct)).Result |> ignore
            (s1.Stores.Conversations.CreateAsync(Conversation.empty convId rec0.Workspace.Id "kept", ct)).Result
            |> ignore
            (s1.Stores.Conversations.AppendEventAsync(convId, userEvent "first", ct)).Result |> ignore
            (s1.Stores.Settings.SaveAsync({ DesignData.defaultAppearance with Transparency = false }, ct)).Result
            |> ignore
        finally
            (s1 :> IDisposable).Dispose()

        // Session 2: a fresh connection sees everything.
        let s2 = SqliteStores.create path DesignData.defaultAppearance

        try
            Assert.Single((s2.Stores.Repositories.ListAsync ct).Result) |> ignore
            let ws = (s2.Stores.Workspaces.GetAsync(rec0.Workspace.Id, ct)).Result.Value
            Assert.Equal(rec0.WorktreePath, ws.WorktreePath)
            let conv = (s2.Stores.Conversations.GetAsync(convId, ct)).Result.Value
            Assert.Equal(1, conv.Messages.Length)
            Assert.Equal("kept", conv.Title)
            Assert.False((s2.Stores.Settings.LoadAsync ct).Result.Transparency)
        finally
            (s2 :> IDisposable).Dispose()
    finally
        try File.Delete path with _ -> ()
