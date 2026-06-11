module Avelia.Services.Tests.WorkspaceServiceTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services

let private ct = CancellationToken.None
let private now () = DateTimeOffset.UnixEpoch

let private addRepo (store: IRepositoryStore) =
    let repo =
        { Id = RepositoryId.create ()
          Name = "widgets"
          Path = RepoPath.Create "C:/repos/widgets"
          DefaultBase = BranchName.Create "main"
          IsOpen = true }

    (store.UpsertAsync(repo, ct)).Result |> ignore
    repo

/// Build a WorkspaceService over real in-memory stores + a fake git layer,
/// recording disposed conversation ids.
let private mkService (stores: Stores) (git: IGitOperations) (disposed: ResizeArray<ConversationId>) =
    WorkspaceService(
        stores.Workspaces,
        stores.Repositories,
        stores.Conversations,
        stores.Settings,
        git,
        FakeGitInspection(),
        "C:/wt",
        now,
        (fun cid ->
            disposed.Add cid
            Task.FromResult())
    )
    :> IWorkspaceService

[<Fact>]
let ``CreateAsync materializes worktree, conversation and workspace record`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let svc = mkService stores git (ResizeArray())
    let branch = BranchName.Create "feature/login"

    match (svc.CreateAsync(repo.Id, branch, BranchName.Create "main", ct)).Result with
    | Success ws ->
        Assert.Equal(branch, ws.Branch)
        Assert.Equal(repo.Id, ws.RepoId)
        Assert.Equal(1, git.WorktreeAddCalls)
        Assert.Equal(repo.Path.Value, git.LastWorktreeRepo)
        // Worktree path is grouped under the configured root.
        let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value
        Assert.StartsWith("C:/wt", record.WorktreePath.Value.Replace('\\', '/'))
        // A conversation was created and bound to the workspace.
        Assert.True((stores.Conversations.GetByWorkspaceAsync(ws.Id, ct)).Result.IsSuccess)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``CreateAsync fails when the repo is unknown and touches no git`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let git = FakeGitOperations()
    let svc = mkService stores git (ResizeArray())

    match (svc.CreateAsync(RepositoryId.create (), BranchName.Create "x", BranchName.Create "main", ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

    Assert.Equal(0, git.WorktreeAddCalls)

[<Fact>]
let ``CreateAsync persists nothing when the worktree add fails`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories

    let git =
        FakeGitOperations(Failure(AveliaError.External("git", "branch already checked out")))

    let svc = mkService stores git (ResizeArray())

    match (svc.CreateAsync(repo.Id, BranchName.Create "dup", BranchName.Create "main", ct)).Result with
    | Failure(AveliaError.External("git", _)) -> ()
    | other -> failwithf "unexpected %A" other

    Assert.Empty((stores.Workspaces.ListAllAsync ct).Result)
    // No orphan conversation either.
    Assert.False((stores.Conversations.GetByWorkspaceAsync(WorkspaceId.create (), ct)).Result.IsSuccess)

[<Fact>]
let ``CreateAsync auto-names the branch from the rail pool given the empty sentinel`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let svc = mkService stores git (ResizeArray())

    // Unchecked.defaultof<BranchName> is the empty sentinel meaning "auto-name".
    match (svc.CreateAsync(repo.Id, Unchecked.defaultof<BranchName>, BranchName.Create "main", ct)).Result with
    | Success ws ->
        Assert.False(String.IsNullOrWhiteSpace ws.Branch.Value)
        Assert.Contains(ws.Branch.Value, WorktreeNames.all)
        // The worktree was materialized for the generated branch.
        Assert.Equal(ws.Branch.Value, git.LastWorktreeBranch)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``SetAgentConfigAsync persists model + thinking + context and disposes the session`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let disposed = ResizeArray<ConversationId>()
    let svc = mkService stores (FakeGitOperations()) disposed

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

    let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value

    match (svc.SetAgentConfigAsync(ws.Id, Opus41, "high", "long_context", ct)).Result with
    | Success updated ->
        Assert.Equal(Opus41, updated.Agent)
        Assert.Equal("high", updated.ReasoningEffort)
        Assert.Equal("long_context", updated.ContextTier)
        // Persisted to the store.
        let reread = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace
        Assert.Equal("high", reread.ReasoningEffort)
        Assert.Equal("long_context", reread.ContextTier)
        // Session torn down so the next message restarts with the new config.
        Assert.Contains(record.ConversationId, disposed)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``DeleteAsync removes the worktree, disposes the session, and drops the record`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let disposed = ResizeArray<ConversationId>()
    let svc = mkService stores git disposed

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "doomed", BranchName.Create "main", ct)).Result.Value

    let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value

    match (svc.DeleteAsync(ws.Id, ct)).Result with
    | Success() ->
        Assert.Contains(record.ConversationId, disposed)
        Assert.False((stores.Workspaces.GetAsync(ws.Id, ct)).Result.IsSuccess)
        Assert.Empty((stores.Workspaces.ListAllAsync ct).Result)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``DeleteAsync fails for an unknown workspace`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    match (svc.DeleteAsync(WorkspaceId.create (), ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``ArchiveAsync disposes the session and flips status`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let disposed = ResizeArray<ConversationId>()
    let svc = mkService stores git disposed

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

    // Draft can't archive directly; move to Active first (store-level).
    let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value

    (stores.Workspaces.UpsertAsync(
        { record with
            Workspace =
                { record.Workspace with
                    Status = WorkspaceStatus.Active } },
        ct
    ))
        .Result
    |> ignore

    match (svc.ArchiveAsync(ws.Id, ct)).Result with
    | Success() ->
        Assert.Contains(record.ConversationId, disposed)
        Assert.Equal(WorkspaceStatus.Archived, (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace.Status)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``UpdateStatusAsync persists a legal transition`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

    // Fresh workspaces are Draft; Draft -> Working is legal.
    match (svc.UpdateStatusAsync(ws.Id, WorkspaceStatus.Working, ct)).Result with
    | Success updated ->
        Assert.True(updated.Status.IsWorking)
        let reread = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace
        Assert.True(reread.Status.IsWorking)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``UpdateStatusAsync rejects an illegal transition and leaves status unchanged`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

    // Drive to Archived, from which Working is not reachable.
    let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value

    (stores.Workspaces.UpsertAsync(
        { record with
            Workspace =
                { record.Workspace with
                    Status = WorkspaceStatus.Archived } },
        ct
    ))
        .Result
    |> ignore

    match (svc.UpdateStatusAsync(ws.Id, WorkspaceStatus.Working, ct)).Result with
    | Failure(AveliaError.Conflict _) ->
        Assert.Equal(WorkspaceStatus.Archived, (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace.Status)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``UpdateStatusAsync fails for an unknown workspace`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    match (svc.UpdateStatusAsync(WorkspaceId.create (), WorkspaceStatus.Working, ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``ListAll projects the shell-facing workspace out of the record`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

    let all = (svc.ListAllAsync ct).Result
    Assert.Single all |> ignore
    Assert.Equal(ws.Id, all.[0].Id)

[<Fact>]
let ``RenameAsync renames the git branch, persists the title and the slug`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let svc = mkService stores git (ResizeArray())

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "speedbird", BranchName.Create "main", ct)).Result.Value

    let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value

    match (svc.RenameAsync(ws.Id, "Add MCP Server", ct)).Result with
    | Success updated ->
        Assert.Equal("Add MCP Server", updated.Title)
        Assert.Equal("add-mcp-server", updated.Branch.Value)
        // The git branch was renamed from the worktree.
        Assert.Equal(1, git.BranchRenameCalls)
        Assert.Equal(record.WorktreePath.Value, git.LastRenameWorktree)
        Assert.Equal("add-mcp-server", git.LastRenameBranch)
        // Persisted.
        let reread = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace
        Assert.Equal("Add MCP Server", reread.Title)
        Assert.Equal("add-mcp-server", reread.Branch.Value)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``RenameAsync rejects a title with no slug-able characters and touches no git`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let svc = mkService stores git (ResizeArray())

    let ws =
        (svc.CreateAsync(repo.Id, BranchName.Create "speedbird", BranchName.Create "main", ct)).Result.Value

    match (svc.RenameAsync(ws.Id, "!!!", ct)).Result with
    | Failure(AveliaError.Validation _) -> ()
    | other -> failwithf "unexpected %A" other

    Assert.Equal(0, git.BranchRenameCalls)
    // Branch unchanged.
    Assert.Equal("speedbird", (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value.Workspace.Branch.Value)

[<Fact>]
let ``RenameAsync fails for an unknown workspace`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())

    match (svc.RenameAsync(WorkspaceId.create (), "Anything", ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "unexpected %A" other
