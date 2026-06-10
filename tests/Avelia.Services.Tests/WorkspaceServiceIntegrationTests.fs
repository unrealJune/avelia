module Avelia.Services.Tests.WorkspaceServiceIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services
open Avelia.Vcs.Git

let private ct = CancellationToken.None

let private git (cwd: string) (args: string array) =
    let r = (GitProcess.runAsync cwd args ct).Result

    if r.ExitCode <> 0 then
        failwithf "git %s failed: %s" (String.concat " " args) r.StdErr

/// Initialize a temp repo with one commit on main; returns its root path.
let private initRepo () =
    let root =
        Path.Combine(Path.GetTempPath(), "avelia-svc-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    git root [| "init"; "-b"; "main" |]
    git root [| "config"; "user.email"; "avelia@test.local" |]
    git root [| "config"; "user.name"; "Avelia Test" |]
    git root [| "config"; "commit.gpgsign"; "false" |]
    File.WriteAllText(Path.Combine(root, "README.md"), "# initial\n")
    git root [| "add"; "README.md" |]
    git root [| "commit"; "-m"; "initial" |]
    root

let private cleanup (path: string) =
    try
        for f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
            try
                File.SetAttributes(f, FileAttributes.Normal)
            with _ ->
                ()

        Directory.Delete(path, true)
    with _ ->
        ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``CreateAsync materializes a real worktree on disk`` () =
    let repoRoot = initRepo ()

    let worktreesRoot =
        Path.Combine(Path.GetTempPath(), "avelia-wt-" + Guid.NewGuid().ToString("N"))

    try
        let stores = InMemoryStores.create DesignData.defaultAppearance

        let repo =
            { Id = RepositoryId.create ()
              Name = "intrepo"
              Path = RepoPath.Create repoRoot
              DefaultBase = BranchName.Create "main"
              IsOpen = true }

        (stores.Repositories.UpsertAsync(repo, ct)).Result |> ignore

        let svc =
            WorkspaceService(
                stores.Workspaces,
                stores.Repositories,
                stores.Conversations,
                stores.Settings,
                GitCli(),
                GitInspector(),
                worktreesRoot,
                (fun () -> DateTimeOffset.UtcNow),
                (fun _ -> Task.FromResult())
            )
            :> IWorkspaceService

        match (svc.CreateAsync(repo.Id, BranchName.Create "feature/work", BranchName.Create "main", ct)).Result with
        | Success ws ->
            let record = (stores.Workspaces.GetAsync(ws.Id, ct)).Result.Value
            Assert.True(Directory.Exists record.WorktreePath.Value, "worktree directory should exist on disk")

            Assert.True(
                File.Exists(Path.Combine(record.WorktreePath.Value, "README.md")),
                "worktree should contain the repo's tracked files"
            )
        | Failure e -> failwithf "expected success, got %A" e
    finally
        cleanup worktreesRoot
        cleanup repoRoot
