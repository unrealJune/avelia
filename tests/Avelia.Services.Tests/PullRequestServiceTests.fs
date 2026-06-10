module Avelia.Services.Tests.PullRequestServiceTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services
open Avelia.Vcs.GitHub

let private ct = CancellationToken.None

/// Scripted <c>IGitHubClient</c>: records the coordinate/number it was asked
/// for and returns fixed results for the get / create / merge paths. Other
/// surfaces are inert.
type private FakeGitHubClient
    (pr: OperationResult<PullRequest>, ?createResult: OperationResult<PullRequest>, ?mergeResult: OperationResult<unit>)
    =
    member val LastNumber = -1 with get, set
    member val LastCoord = Unchecked.defaultof<RepoCoordinate> with get, set
    member val LastCreateRequest: CreatePrRequest option = None with get, set
    member val LastMergeNumber = -1 with get, set
    member val LastMergeMethod: PrMergeMethod option = None with get, set

    interface IGitHubClient with
        member this.GetPullRequestAsync(repo, number, _ct) =
            this.LastCoord <- repo
            this.LastNumber <- number
            Task.FromResult pr

        member _.ListUserReposAsync(_ct) =
            Task.FromResult(Success(([||]: RepoSummary[]) :> IReadOnlyList<_>))

        member _.ListPrsForUserAsync(_ct) =
            Task.FromResult(Success(([||]: PullRequest[]) :> IReadOnlyList<_>))

        member this.CreatePullRequestAsync(req, _ct) =
            this.LastCreateRequest <- Some req
            Task.FromResult(defaultArg createResult (Failure AveliaError.Unauthorized))

        member this.MergePullRequestAsync(repo, number, method, _ct) =
            this.LastCoord <- repo
            this.LastMergeNumber <- number
            this.LastMergeMethod <- Some method
            Task.FromResult(defaultArg mergeResult (Success()))

        member _.CommentAsync(_repo, _num, _body, _ct) = Task.FromResult(Success())

        member _.ListNotificationsAsync(_since, _ct) =
            Task.FromResult(Success(([||]: Notification[]) :> IReadOnlyList<_>))

        member _.LastRateLimit = ValueNone

let private seed (stores: Stores) (prNumber: int) =
    let repoId = RepositoryId.create ()

    let repo: Repository =
        { Id = repoId
          Name = "widgets"
          Path = RepoPath.Create "C:/repos/widgets"
          DefaultBase = BranchName.Create "main"
          IsOpen = true }

    (stores.Repositories.UpsertAsync(repo, ct)).Result |> ignore
    let wsId = WorkspaceId.create ()

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
          PrNumber = prNumber
          ReasoningEffort = ""
          ContextTier = "" }

    let record =
        { Workspace = ws
          WorktreePath = RepoPath.Create "C:/wt/x"
          ConversationId = ConversationId.create () }

    (stores.Workspaces.UpsertAsync(record, ct)).Result |> ignore
    wsId

let private samplePr: PullRequest =
    { Id = PullRequestId 42
      Number = 42
      Title = "Add login"
      Branch = BranchName.Create "feature/x"
      Base = BranchName.Create "main"
      Status = PrStatus.Open
      Checks = [||]
      MergeReady = true }

let private mkWith (getClient) (stores: Stores) (inspection: IGitInspection) (gitOps: IGitOperations) =
    PullRequestService(getClient, stores.Workspaces, stores.Repositories, inspection, gitOps) :> IPullRequestService

let private mk (getClient) (stores: Stores) (inspection: IGitInspection) =
    mkWith getClient stores inspection (FakeGitOperations())

[<Fact>]
let ``GetForWorkspaceAsync returns NotFound for a workspace with no PR`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 0
    let client = FakeGitHubClient(Success samplePr)

    let svc =
        mk (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (FakeGitInspection())

    match (svc.GetForWorkspaceAsync(wsId, ct)).Result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> failwithf "expected NotFound, got %A" other

[<Fact>]
let ``GetForWorkspaceAsync resolves the origin coordinate and returns the live PR`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 42
    let client = FakeGitHubClient(Success samplePr)

    let inspection =
        FakeGitInspection(remoteUrlResult = Success "git@github.com:acme/widgets.git")

    let svc =
        mk (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores inspection

    match (svc.GetForWorkspaceAsync(wsId, ct)).Result with
    | Success pr ->
        Assert.Equal(42, pr.Number)
        Assert.Equal(42, client.LastNumber)
        Assert.Equal("acme", client.LastCoord.Owner)
        Assert.Equal("widgets", client.LastCoord.Name)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``GetForWorkspaceAsync surfaces a client-resolution failure`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 7

    let svc =
        mk
            (fun _ -> Task.FromResult((Failure AveliaError.Unauthorized): OperationResult<IGitHubClient>))
            stores
            (FakeGitInspection())

    match (svc.GetForWorkspaceAsync(wsId, ct)).Result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> failwithf "expected Unauthorized, got %A" other

let private githubInspection () =
    FakeGitInspection(remoteUrlResult = Success "git@github.com:acme/widgets.git")

[<Fact>]
let ``CreateForWorkspaceAsync rejects an empty title before any network call`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 0
    let client = FakeGitHubClient(Success samplePr)
    let gitOps = FakeGitOperations()

    let svc =
        mkWith (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ()) gitOps

    match (svc.CreateForWorkspaceAsync(wsId, "   ", "", false, ct)).Result with
    | Failure(AveliaError.Validation _) ->
        Assert.Equal(0, gitOps.PushCalls)
        Assert.True(client.LastCreateRequest.IsNone)
    | other -> failwithf "expected Validation, got %A" other

[<Fact>]
let ``CreateForWorkspaceAsync refuses a workspace that already has a PR`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 42
    let client = FakeGitHubClient(Success samplePr)
    let gitOps = FakeGitOperations()

    let svc =
        mkWith (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ()) gitOps

    match (svc.CreateForWorkspaceAsync(wsId, "Add login", "", false, ct)).Result with
    | Failure(AveliaError.Conflict _) ->
        Assert.Equal(0, gitOps.PushCalls)
        Assert.True(client.LastCreateRequest.IsNone)
    | other -> failwithf "expected Conflict, got %A" other

[<Fact>]
let ``CreateForWorkspaceAsync pushes the branch, opens the PR and records its number`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 0

    let created =
        { samplePr with
            Id = PullRequestId 123
            Number = 123
            Title = "Add login" }

    let client = FakeGitHubClient(Success samplePr, createResult = Success created)
    let gitOps = FakeGitOperations()

    let svc =
        mkWith (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ()) gitOps

    match (svc.CreateForWorkspaceAsync(wsId, "  Add login  ", "body", false, ct)).Result with
    | Success pr ->
        Assert.Equal(123, pr.Number)
        // Branch must be pushed before the PR is opened.
        Assert.Equal(1, gitOps.PushCalls)
        Assert.True(client.LastCreateRequest.IsSome)
        Assert.Equal("Add login", client.LastCreateRequest.Value.Title)
        // The new number is persisted so subsequent loads resolve a live PR.
        match (stores.Workspaces.GetAsync(wsId, ct)).Result with
        | Success record -> Assert.Equal(123, record.Workspace.PrNumber)
        | Failure e -> failwithf "expected workspace, got %A" e
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``CreateForWorkspaceAsync surfaces a push failure without creating a PR`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 0
    let client = FakeGitHubClient(Success samplePr, createResult = Success samplePr)

    let gitOps =
        FakeGitOperations(pushResult = Failure(AveliaError.External("git", "push rejected")))

    let svc =
        mkWith (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ()) gitOps

    match (svc.CreateForWorkspaceAsync(wsId, "Add login", "", false, ct)).Result with
    | Failure(AveliaError.External _) ->
        Assert.True(client.LastCreateRequest.IsNone)

        match (stores.Workspaces.GetAsync(wsId, ct)).Result with
        | Success record -> Assert.Equal(0, record.Workspace.PrNumber)
        | Failure e -> failwithf "expected workspace, got %A" e
    | other -> failwithf "expected External failure, got %A" other

[<Fact>]
let ``MergeForWorkspaceAsync returns NotFound for a workspace with no PR`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 0
    let client = FakeGitHubClient(Success samplePr)

    let svc =
        mk (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ())

    match (svc.MergeForWorkspaceAsync(wsId, PrMergeMethod.Squash, ct)).Result with
    | Failure(AveliaError.NotFound _) -> Assert.Equal(-1, client.LastMergeNumber)
    | other -> failwithf "expected NotFound, got %A" other

[<Fact>]
let ``MergeForWorkspaceAsync forwards the workspace PR number and strategy to the client`` () =
    let stores = InMemoryStores.create DesignData.defaultAppearance
    let wsId = seed stores 42
    let client = FakeGitHubClient(Success samplePr, mergeResult = Success())

    let svc =
        mk (fun _ -> Task.FromResult(Success(client :> IGitHubClient))) stores (githubInspection ())

    match (svc.MergeForWorkspaceAsync(wsId, PrMergeMethod.Squash, ct)).Result with
    | Success() ->
        Assert.Equal(42, client.LastMergeNumber)
        Assert.Equal(Some PrMergeMethod.Squash, client.LastMergeMethod)
        Assert.Equal("acme", client.LastCoord.Owner)
    | Failure e -> failwithf "expected success, got %A" e
