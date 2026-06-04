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
          PrNumber = 0 }

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
    let svc = InteractiveTerminalService(stores.Workspaces, factory) :> ITerminalLaunchService

    match (svc.StartAsync(wsId, ct)).Result with
    | Success session ->
        Assert.NotNull(session.Terminal)
        Assert.Equal("C:/wt/abc", factory.LastInteractiveWorkspace)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``StartAsync fails for an unknown workspace`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let svc = InteractiveTerminalService(stores.Workspaces, FakeAgentSessionFactory()) :> ITerminalLaunchService

    match (svc.StartAsync(WorkspaceId.create (), ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other
