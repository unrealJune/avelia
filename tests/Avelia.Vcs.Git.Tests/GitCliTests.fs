module Avelia.Vcs.Git.Tests.GitCliTests

open System
open System.IO
open System.Threading
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.Git
open Avelia.Vcs.Git.Tests.TempRepo

let private ct = CancellationToken.None

let private assertSuccess (label: string) (r: OperationResult<'T>) : 'T =
    match r with
    | Success v -> v
    | Failure e -> failwithf "%s: %A" label e

/// Build a sibling-directory path next to <paramref name="repoPath"/>. Uses
/// <c>Path.GetFullPath</c> so the resulting string has no <c>..</c> segments
/// and survives <c>RepoPath.Create</c>'s validation.
let private siblingPath (repoPath: string) (name: string) : RepoPath =
    let parent = Path.GetFullPath(Path.Combine(repoPath, ".."))
    RepoPath.Create(Path.Combine(parent, name))

[<Trait("Category", "Integration")>]
[<Fact>]
let ``WorktreeAdd creates a new branch and returns a Worktree`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations
    let branch = BranchName.Create "feature/foo"
    let worktreePath = siblingPath repo.Path ("wt-" + Guid.NewGuid().ToString("N"))

    let result =
        cli.WorktreeAddAsync(repo.RepoPath, branch, worktreePath, ct).GetAwaiter().GetResult()

    let wt = assertSuccess "WorktreeAddAsync" result
    Assert.Equal(branch, wt.Branch)
    Assert.True(Directory.Exists wt.Path.Value)
    Assert.Equal(40, wt.Head.Value.Length)

    // Tidy up so the parent directory isn't littered after the test.
    let _ = cli.WorktreeRemoveAsync(wt.Path, true, ct).GetAwaiter().GetResult()
    ()

[<Trait("Category", "Integration")>]
[<Fact>]
let ``WorktreeRemove force-removes a worktree`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations
    let branch = BranchName.Create "feature/remove-me"
    let worktreePath = siblingPath repo.Path ("wt-" + Guid.NewGuid().ToString("N"))

    let _ =
        cli.WorktreeAddAsync(repo.RepoPath, branch, worktreePath, ct).GetAwaiter().GetResult()
        |> assertSuccess "setup WorktreeAddAsync"

    let result =
        cli.WorktreeRemoveAsync(worktreePath, true, ct).GetAwaiter().GetResult()

    assertSuccess "WorktreeRemoveAsync" result |> ignore
    Assert.False(Directory.Exists worktreePath.Value)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Commit returns the new HEAD sha`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations
    repo.WriteFile("a.txt", "hello\n")
    repo.StageFile "a.txt"

    let result =
        cli.CommitAsync(repo.RepoPath, CommitMessage.Create "add a", ct).GetAwaiter().GetResult()

    let sha = assertSuccess "CommitAsync" result
    Assert.Equal(40, sha.Value.Length)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``BranchCreate then Checkout switches branches in a worktree`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations

    let main = BranchName.Create "main"
    let target = BranchName.Create "feature/checkout-test"

    cli.BranchCreateAsync(repo.RepoPath, target, main, ct).GetAwaiter().GetResult()
    |> assertSuccess "BranchCreateAsync"
    |> ignore

    cli.CheckoutAsync(repo.RepoPath, target, ct).GetAwaiter().GetResult()
    |> assertSuccess "CheckoutAsync"
    |> ignore

    // Sanity: the working tree should now be on the target branch.
    let revParse =
        (GitProcess.runAsync repo.Path [| "rev-parse"; "--abbrev-ref"; "HEAD" |] ct).Result

    Assert.Equal(0, revParse.ExitCode)
    Assert.Equal("feature/checkout-test", GitProcess.trimmedStdOut revParse)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``BranchDelete removes a branch from the repo`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations

    let main = BranchName.Create "main"
    let temp = BranchName.Create "feature/delete-me"

    cli.BranchCreateAsync(repo.RepoPath, temp, main, ct).GetAwaiter().GetResult()
    |> assertSuccess "BranchCreateAsync"
    |> ignore

    cli.BranchDeleteAsync(repo.RepoPath, temp, ct).GetAwaiter().GetResult()
    |> assertSuccess "BranchDeleteAsync"
    |> ignore

    let listBranches =
        (GitProcess.runAsync repo.Path [| "branch"; "--list"; "feature/delete-me" |] ct).Result

    Assert.Equal(0, listBranches.ExitCode)
    Assert.True(String.IsNullOrWhiteSpace listBranches.StdOut)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Fetch against a nonexistent remote returns External error`` () =
    use repo = new TempRepo()
    let cli = GitCli() :> IGitOperations
    let bogus = Remote.Create "no-such-remote"

    let result = cli.FetchAsync(repo.RepoPath, bogus, ct).GetAwaiter().GetResult()

    match result with
    | Success _ -> Assert.Fail "Expected failure for nonexistent remote"
    | Failure(AveliaError.External(source, _)) -> Assert.Equal("git", source)
    | Failure other -> Assert.Fail $"Expected External, got {other}"

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Cancellation kills the subprocess and surfaces OperationCanceledException`` () =
    use repo = new TempRepo()
    // Fetch from a black-hole address that's guaranteed to hang in connect.
    // Using a private-range IP keeps the test independent of internet access.
    let bogusUrl = "https://10.255.255.1/no.git"

    let setRemote =
        (GitProcess.runAsync repo.Path [| "remote"; "add"; "slow"; bogusUrl |] CancellationToken.None).Result

    Assert.Equal(0, setRemote.ExitCode)

    let cli = GitCli() :> IGitOperations
    use cts = new CancellationTokenSource()
    cts.CancelAfter(TimeSpan.FromMilliseconds 250.0)

    let mutable cancelled = false

    try
        cli.FetchAsync(repo.RepoPath, Remote.Create "slow", cts.Token).GetAwaiter().GetResult()
        |> ignore
    with
    | :? OperationCanceledException -> cancelled <- true
    | :? AggregateException as ae when ae.InnerExceptions |> Seq.exists (fun e -> e :? OperationCanceledException) ->
        cancelled <- true

    Assert.True(cancelled, "Expected OperationCanceledException")
