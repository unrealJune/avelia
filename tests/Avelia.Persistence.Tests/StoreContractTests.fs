module Avelia.Persistence.Tests.StoreContractTests

open System
open System.IO
open System.Threading
open Xunit
open Microsoft.Data.Sqlite
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
          PrNumber = 7
          ReasoningEffort = ""
          ContextTier = "" }

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
    (stores.Conversations.CreateAsync(Conversation.empty convId rec0.Workspace.Id "T", ct)).Result
    |> ignore

    Assert.True((stores.Conversations.GetByWorkspaceAsync(rec0.Workspace.Id, ct)).Result.IsSuccess)

    let afterFirst =
        (stores.Conversations.AppendEventAsync(convId, userEvent "one", ct)).Result.Value

    Assert.Equal(1, afterFirst.LastSequence)

    let afterSecond =
        (stores.Conversations.AppendEventAsync(convId, userEvent "two", ct)).Result.Value

    Assert.Equal(2, afterSecond.LastSequence)
    Assert.Equal(2, (stores.Conversations.GetAsync(convId, ct)).Result.Value.Messages.Length)

    match (stores.Conversations.AppendEventAsync(ConversationId.create (), userEvent "x", ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "expected NotFound, got %A" other

    // Settings
    let changed =
        { DesignData.defaultAppearance with
            Density = Density.Compact
            Accent = AccentChoice.Sage }

    (stores.Settings.SaveAsync(changed, ct)).Result |> ignore
    Assert.Equal(changed, (stores.Settings.LoadAsync ct).Result)

[<Fact>]
let ``in-memory stores satisfy the contract`` () =
    runContract (InMemoryStores.create DesignData.defaultAppearance)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``sqlite stores satisfy the contract`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "avelia-sql-" + Guid.NewGuid().ToString("N") + ".db")

    let set = SqliteStores.create path DesignData.defaultAppearance

    try
        try
            runContract set.Stores
        finally
            (set :> IDisposable).Dispose()
    finally
        try
            File.Delete path
        with _ ->
            ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``sqlite persists across reopen`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "avelia-sql-" + Guid.NewGuid().ToString("N") + ".db")

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

            (s1.Stores.Conversations.AppendEventAsync(convId, userEvent "first", ct)).Result
            |> ignore

            (s1.Stores.Settings.SaveAsync(
                { DesignData.defaultAppearance with
                    Transparency = false },
                ct
            ))
                .Result
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
        try
            File.Delete path
        with _ ->
            ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``sqlite reconciles a legacy extended_thinking settings table on open`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "avelia-sql-" + Guid.NewGuid().ToString("N") + ".db")

    try
        // Seed a settings table in the pre-unified-model-bar shape: the boolean
        // `extended_thinking` column the schema since dropped, still NOT NULL with
        // no default. The current upsert never supplies it, so without the drop
        // migration every settings write fails its INSERT arm with a NOT NULL
        // violation — which is what crashed the app at startup.
        let connStr = SqliteConnectionStringBuilder(DataSource = path).ToString()

        do
            use conn = new SqliteConnection(connStr)
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """CREATE TABLE settings (
                     id INTEGER PRIMARY KEY CHECK (id = 1), accent TEXT NOT NULL, density TEXT NOT NULL,
                     transparency INTEGER NOT NULL, open_with_right_panel INTEGER NOT NULL,
                     default_model TEXT NOT NULL, extended_thinking INTEGER NOT NULL,
                     reasoning_effort TEXT NOT NULL DEFAULT '', context_tier TEXT NOT NULL DEFAULT '');
                   INSERT INTO settings (id, accent, density, transparency, open_with_right_panel, default_model, extended_thinking, reasoning_effort, context_tier)
                     VALUES (1, 'skyblue', 'comfortable', 1, 1, 'custom:claude-opus-4.8', 1, '', '');"""

            cmd.ExecuteNonQuery() |> ignore

        // Opening runs the migrations (which drop the orphan); a settings save
        // must then succeed and round-trip rather than throw.
        let set = SqliteStores.create path DesignData.defaultAppearance

        try
            let changed =
                { DesignData.defaultAppearance with
                    Density = Density.Compact
                    Accent = AccentChoice.Sage }

            (set.Stores.Settings.SaveAsync(changed, ct)).Result |> ignore
            Assert.Equal(changed, (set.Stores.Settings.LoadAsync ct).Result)
        finally
            (set :> IDisposable).Dispose()
    finally
        SqliteConnection.ClearAllPools()

        try
            File.Delete path
        with _ ->
            ()
