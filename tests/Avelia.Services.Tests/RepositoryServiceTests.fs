module Avelia.Services.Tests.RepositoryServiceTests

open System.Threading
open Xunit
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services

let private ct = CancellationToken.None
let private path = RepoPath.Create "C:/repos/widgets"
let private main = BranchName.Create "main"

[<Fact>]
let ``AddAsync on a valid repo persists and derives the name`` () =
    let store = InMemoryRepositoryStore()
    let svc = RepositoryService(store, FakeGitInspection()) :> IRepositoryService

    match (svc.AddAsync(path, main, ct)).Result with
    | Success repo ->
        Assert.Equal("widgets", repo.Name)
        Assert.Equal(path, repo.Path)
        // Persisted in the store.
        Assert.Equal(Success repo, ((store :> IRepositoryStore).GetAsync(repo.Id, ct)).Result)
    | Failure e -> failwithf "expected success, got %A" e

[<Fact>]
let ``AddAsync rejects a non-repository and persists nothing`` () =
    let store = InMemoryRepositoryStore()

    let inspection =
        FakeGitInspection(Failure(AveliaError.External("git", "not a git repository")))

    let svc = RepositoryService(store, inspection) :> IRepositoryService

    match (svc.AddAsync(path, main, ct)).Result with
    | Failure(AveliaError.External("git", _)) -> ()
    | other -> failwithf "unexpected %A" other

    Assert.Empty(((store :> IRepositoryStore).ListAsync ct).Result)

[<Fact>]
let ``List and Remove delegate to the store`` () =
    let store = InMemoryRepositoryStore()
    let svc = RepositoryService(store, FakeGitInspection()) :> IRepositoryService
    let repo = (svc.AddAsync(path, main, ct)).Result.Value

    Assert.Single((svc.ListAsync ct).Result) |> ignore
    (svc.RemoveAsync(repo.Id, ct)).Result |> ignore
    Assert.Empty((svc.ListAsync ct).Result)
