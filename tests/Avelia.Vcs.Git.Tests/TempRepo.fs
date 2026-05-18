module Avelia.Vcs.Git.Tests.TempRepo

open System
open System.IO
open System.Threading
open Avelia.Core.Abstractions
open Avelia.Vcs.Git

let private runOrFail (cwd: string) (args: string array) =
    let r = (GitProcess.runAsync cwd args CancellationToken.None).Result

    if r.ExitCode <> 0 then
        failwithf "git %s failed in %s: %s" (String.concat " " args) cwd r.StdErr

/// Disposable wrapper around a temp directory hosting a git repo. Initialized
/// with one commit on <c>main</c> so HEAD resolves and subsequent worktree
/// adds have a base ref to branch from.
type TempRepo() =
    let suffix = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), "avelia-test-" + suffix)

    do
        Directory.CreateDirectory root |> ignore
        // --initial-branch=main so behavior is stable across users who have
        // or haven't set init.defaultBranch globally.
        runOrFail root [| "init"; "-b"; "main" |]
        runOrFail root [| "config"; "user.email"; "avelia@test.local" |]
        runOrFail root [| "config"; "user.name"; "Avelia Test" |]
        // Even users with global commit.gpgsign=true should not have these
        // commits hang on a passphrase prompt.
        runOrFail root [| "config"; "commit.gpgsign"; "false" |]

        File.WriteAllText(Path.Combine(root, "README.md"), "# initial\n")
        runOrFail root [| "add"; "README.md" |]
        runOrFail root [| "commit"; "-m"; "initial" |]

    /// Absolute path to the repo root.
    member _.Path = root

    /// Typed primitive (matches the abstractions surface).
    member _.RepoPath = RepoPath.Create root

    /// Write text to a file inside the repo (creates parent dirs).
    member _.WriteFile(relative: string, contents: string) =
        let full = Path.Combine(root, relative)

        match Path.GetDirectoryName full with
        | null -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        File.WriteAllText(full, contents)

    /// Stage a file via <c>git add</c>.
    member _.StageFile(relative: string) = runOrFail root [| "add"; relative |]

    interface IDisposable with
        member _.Dispose() =
            try
                // git ships read-only files under .git/objects; clear bits
                // before deletion or Directory.Delete throws on Windows.
                for f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                    try
                        File.SetAttributes(f, FileAttributes.Normal)
                    with _ ->
                        ()

                Directory.Delete(root, true)
            with _ ->
                ()
