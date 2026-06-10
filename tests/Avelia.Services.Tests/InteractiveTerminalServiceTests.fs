module Avelia.Services.Tests.InteractiveTerminalServiceTests

open System
open System.Threading
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services

let private ct = CancellationToken.None

let private seedWorkspace (stores: Stores) (worktree: string) =
    let wsId = WorkspaceId.create ()

    let ws: Workspace =
        { Id = wsId
          RepoId = RepositoryId.create ()
          Branch = BranchName.Create "f"
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
          WorktreePath = RepoPath.Create worktree
          ConversationId = ConversationId.create () }

    (stores.Workspaces.UpsertAsync(record, ct)).Result |> ignore
    wsId

[<Fact>]
let ``StartAsync launches an interactive session in the workspace worktree`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seedWorkspace stores "C:/wt/abc"
    let factory = FakeAgentSessionFactory()

    let svc =
        InteractiveTerminalService(stores.Workspaces, stores.Settings, factory) :> ITerminalLaunchService

    match (svc.StartAsync(wsId, ct)).Result with
    | Success session ->
        Assert.NotNull(session.Terminal)
        Assert.Equal("C:/wt/abc", factory.LastInteractiveWorkspace)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``StartAsync fails for an unknown workspace`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance

    let svc =
        InteractiveTerminalService(stores.Workspaces, stores.Settings, FakeAgentSessionFactory())
        :> ITerminalLaunchService

    match (svc.StartAsync(WorkspaceId.create (), ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``StartAsync threads the settings reasoning effort and context tier into the session config`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seedWorkspace stores "C:/wt/abc"

    let appearance = (stores.Settings.LoadAsync ct).Result

    (stores.Settings.SaveAsync(
        { appearance with
            ReasoningEffort = ReasoningEffort.High
            ContextTier = ContextTier.LongContext },
        ct
    ))
        .Result
    |> ignore

    let factory = FakeAgentSessionFactory()

    let svc =
        InteractiveTerminalService(stores.Workspaces, stores.Settings, factory) :> ITerminalLaunchService

    match (svc.StartAsync(wsId, ct)).Result with
    | Success _ ->
        match factory.LastInteractiveConfig with
        | Some config ->
            Assert.Equal(ReasoningEffort.High, config.ReasoningEffort)
            Assert.Equal(ContextTier.LongContext, config.ContextTier)
        | None -> failwith "expected the factory to capture the session config"
    | Failure e -> failwithf "expected success, got %A" e
