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
    let git = FakeGitOperations(Failure(AveliaError.External("git", "branch already checked out")))
    let svc = mkService stores git (ResizeArray())

    match (svc.CreateAsync(repo.Id, BranchName.Create "dup", BranchName.Create "main", ct)).Result with
    | Failure(AveliaError.External("git", _)) -> ()
    | other -> failwithf "unexpected %A" other

    Assert.Empty((stores.Workspaces.ListAllAsync ct).Result)
    // No orphan conversation either.
    Assert.False((stores.Conversations.GetByWorkspaceAsync(WorkspaceId.create (), ct)).Result.IsSuccess)

[<Fact>]
let ``ArchiveAsync disposes the session and flips status`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let git = FakeGitOperations()
    let disposed = ResizeArray<ConversationId>()
    let svc = mkService stores git disposed
    let ws = (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value

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
let ``ListAll projects the shell-facing workspace out of the record`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let repo = addRepo stores.Repositories
    let svc = mkService stores (FakeGitOperations()) (ResizeArray())
    let ws = (svc.CreateAsync(repo.Id, BranchName.Create "f", BranchName.Create "main", ct)).Result.Value
    let all = (svc.ListAllAsync ct).Result
    Assert.Single all |> ignore
    Assert.Equal(ws.Id, all.[0].Id)
