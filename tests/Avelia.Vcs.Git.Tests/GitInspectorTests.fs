module Avelia.Vcs.Git.Tests.GitInspectorTests

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

/// Sibling-of-repo path, normalized through <c>Path.GetFullPath</c> so the
/// resulting string survives <c>RepoPath.Create</c> validation.
let private siblingPath (repoPath: string) (name: string) : RepoPath =
    let parent = Path.GetFullPath(Path.Combine(repoPath, ".."))
    RepoPath.Create(Path.Combine(parent, name))

let private runOrFail (cwd: string) (args: string array) =
    let r = (GitProcess.runAsync cwd args ct).Result

    if r.ExitCode <> 0 then
        failwithf "git %s failed in %s: %s" (String.concat " " args) cwd r.StdErr

// ---------------------------------------------------------------------------
//  StatusAsync
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Status on a clean repo reports no uncommitted changes`` () =
    use repo = new TempRepo()
    let inspector = GitInspector() :> IGitInspection

    let status =
        inspector.StatusAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "StatusAsync"

    Assert.Equal("main", status.Branch.Value)
    Assert.False(status.HasUncommittedChanges)
    Assert.Empty(status.Files)
    // No upstream → ahead/behind both zero.
    Assert.Equal(0, status.AheadBehind.Ahead)
    Assert.Equal(0, status.AheadBehind.Behind)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Status surfaces an untracked file`` () =
    use repo = new TempRepo()
    repo.WriteFile("notes.md", "scratch\n")
    let inspector = GitInspector() :> IGitInspection

    let status =
        inspector.StatusAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "StatusAsync"

    Assert.True(status.HasUncommittedChanges)

    let file = status.Files |> Seq.find (fun f -> f.Path.Value = "notes.md")
    Assert.True(file.IsUntracked)
    Assert.False(file.IsStaged)
    Assert.False(file.IsConflicted)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Status reports a staged file as IsStaged`` () =
    use repo = new TempRepo()
    repo.WriteFile("staged.txt", "hello\n")
    repo.StageFile "staged.txt"
    let inspector = GitInspector() :> IGitInspection

    let status =
        inspector.StatusAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "StatusAsync"

    Assert.True(status.HasUncommittedChanges)

    let file = status.Files |> Seq.find (fun f -> f.Path.Value = "staged.txt")
    Assert.True(file.IsStaged)
    Assert.False(file.IsUntracked)

// ---------------------------------------------------------------------------
//  LogAsync
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Log returns at least the initial commit`` () =
    use repo = new TempRepo()
    let inspector = GitInspector() :> IGitInspection

    let commits =
        inspector.LogAsync(repo.RepoPath, 10, ct).GetAwaiter().GetResult()
        |> assertSuccess "LogAsync"

    Assert.NotEmpty commits
    let initial = commits.[commits.Count - 1]
    Assert.Equal("initial", initial.Subject)
    Assert.Equal("Avelia Test", initial.Author)
    Assert.Equal(40, initial.Id.Value.Length)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Log honors the limit parameter`` () =
    use repo = new TempRepo()

    // Add a second commit so the limit has something to truncate.
    File.WriteAllText(Path.Combine(repo.Path, "second.txt"), "more\n")
    runOrFail repo.Path [| "add"; "second.txt" |]
    runOrFail repo.Path [| "commit"; "-m"; "second" |]

    let inspector = GitInspector() :> IGitInspection

    let commits =
        inspector.LogAsync(repo.RepoPath, 1, ct).GetAwaiter().GetResult()
        |> assertSuccess "LogAsync"

    Assert.Equal(1, commits.Count)
    Assert.Equal("second", commits.[0].Subject)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Log with limit zero returns empty`` () =
    use repo = new TempRepo()
    let inspector = GitInspector() :> IGitInspection

    let commits =
        inspector.LogAsync(repo.RepoPath, 0, ct).GetAwaiter().GetResult()
        |> assertSuccess "LogAsync"

    Assert.Empty commits

// ---------------------------------------------------------------------------
//  ListBranchesAsync
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
[<Fact>]
let ``ListBranches returns the default branch`` () =
    use repo = new TempRepo()
    let inspector = GitInspector() :> IGitInspection

    let branches =
        inspector.ListBranchesAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "ListBranchesAsync"

    Assert.Contains(branches, fun b -> b.Value = "main")

[<Trait("Category", "Integration")>]
[<Fact>]
let ``ListBranches surfaces new branches`` () =
    use repo = new TempRepo()
    runOrFail repo.Path [| "branch"; "feature/x"; "main" |]
    let inspector = GitInspector() :> IGitInspection

    let branches =
        inspector.ListBranchesAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "ListBranchesAsync"

    Assert.Contains(branches, fun b -> b.Value = "feature/x")

// ---------------------------------------------------------------------------
//  ListWorktreesAsync
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
[<Fact>]
let ``ListWorktrees includes the main checkout`` () =
    use repo = new TempRepo()
    let inspector = GitInspector() :> IGitInspection

    let worktrees =
        inspector.ListWorktreesAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
        |> assertSuccess "ListWorktreesAsync"

    Assert.NotEmpty worktrees
    let main = worktrees |> Seq.find (fun w -> w.Branch.Value = "main")
    Assert.False main.IsLocked
    Assert.Equal(40, main.Head.Value.Length)

[<Trait("Category", "Integration")>]
[<Fact>]
let ``ListWorktrees enumerates a linked worktree`` () =
    use repo = new TempRepo()
    let linkedPath = siblingPath repo.Path ("wt-" + Guid.NewGuid().ToString("N"))

    // Create a linked worktree via git.exe so we test the inspector against
    // a real `.git/worktrees/<n>/` entry rather than the synthetic main one.
    runOrFail repo.Path [| "worktree"; "add"; "-b"; "feature/wt"; linkedPath.Value |]

    let inspector = GitInspector() :> IGitInspection

    try
        let worktrees =
            inspector.ListWorktreesAsync(repo.RepoPath, ct).GetAwaiter().GetResult()
            |> assertSuccess "ListWorktreesAsync"

        Assert.Contains(worktrees, fun w -> w.Branch.Value = "feature/wt")
    finally
        runOrFail repo.Path [| "worktree"; "remove"; "--force"; linkedPath.Value |]
