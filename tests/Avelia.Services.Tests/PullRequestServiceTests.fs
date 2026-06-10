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
/// for and returns a fixed PR result. Other surfaces are inert.
type private FakeGitHubClient(pr: OperationResult<PullRequest>) =
    member val LastNumber = -1 with get, set
    member val LastCoord = Unchecked.defaultof<RepoCoordinate> with get, set

    interface IGitHubClient with
        member this.GetPullRequestAsync(repo, number, _ct) =
            this.LastCoord <- repo
            this.LastNumber <- number
            Task.FromResult pr

        member _.ListUserReposAsync(_ct) =
            Task.FromResult(Success(([||]: RepoSummary[]) :> IReadOnlyList<_>))

        member _.ListPrsForUserAsync(_ct) =
            Task.FromResult(Success(([||]: PullRequest[]) :> IReadOnlyList<_>))

        member _.CreatePullRequestAsync(_req, _ct) =
            Task.FromResult((Failure AveliaError.Unauthorized): OperationResult<PullRequest>)

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

let private mk (getClient) (stores: Stores) (inspection: IGitInspection) =
    PullRequestService(getClient, stores.Workspaces, stores.Repositories, inspection) :> IPullRequestService

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
