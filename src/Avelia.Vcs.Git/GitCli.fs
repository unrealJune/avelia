namespace Avelia.Vcs.Git

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

/// Implementation of <see cref="IGitOperations"/> that shells out to
/// <c>git.exe</c>. Mutating operations are serialized per repository via
/// <see cref="RepoLocks"/>; the resolution from a worktree path to its
/// shared <c>.git</c> directory is cached so the lock keying costs one
/// extra subprocess per *unfamiliar* worktree, not per call.
type GitCli() =

    // worktree-path → common-dir cache. Looking up the shared .git dir
    // requires a `git rev-parse --git-common-dir` invocation; we cache the
    // result so repeated mutations on the same worktree skip the round-trip.
    let commonDirCache = ConcurrentDictionary<string, string>()

    let externalErr (source: string) (r: GitProcessResult) =
        let detail =
            if String.IsNullOrWhiteSpace r.StdErr then
                r.StdOut.TrimEnd()
            else
                r.StdErr.TrimEnd()

        AveliaError.External(source, $"exit {r.ExitCode}: {detail}")

    let runChecked
        (cwd: string)
        (args: string seq)
        (ct: CancellationToken)
        : Task<Result<GitProcessResult, AveliaError>> =
        task {
            let! r = GitProcess.runAsync cwd args ct

            if r.ExitCode = 0 then
                return Ok r
            else
                return Error(externalErr "git" r)
        }

    /// Resolve the canonical lock key for a worktree path. Cached.
    let resolveLockKeyAsync (worktree: RepoPath) (ct: CancellationToken) : Task<Result<string, AveliaError>> =
        task {
            let canonicalWorktree = RepoLocks.canonicalize worktree.Value

            match commonDirCache.TryGetValue canonicalWorktree with
            | true, cached -> return Ok cached
            | _ ->
                let! r = GitProcess.runAsync worktree.Value [ "rev-parse"; "--git-common-dir" ] ct

                if r.ExitCode = 0 then
                    let dir = GitProcess.trimmedStdOut r
                    let key = RepoLocks.canonicalize dir
                    commonDirCache.[canonicalWorktree] <- key
                    return Ok key
                else
                    return Error(externalErr "git" r)
        }

    /// Run <paramref name="work"/> under the repo-scoped lock derived from
    /// the given worktree path.
    let withWorktreeLockAsync
        (worktree: RepoPath)
        (ct: CancellationToken)
        (work: unit -> Task<OperationResult<'T>>)
        : Task<OperationResult<'T>> =
        task {
            let! keyResult = resolveLockKeyAsync worktree ct

            match keyResult with
            | Error e -> return Failure e
            | Ok key -> return! RepoLocks.withLockAsync key ct work
        }

    /// Run <paramref name="work"/> under the repo-scoped lock identified
    /// directly by the repo path (no resolution needed — the input IS the
    /// main checkout).
    let withRepoLockAsync
        (repo: RepoPath)
        (ct: CancellationToken)
        (work: unit -> Task<OperationResult<'T>>)
        : Task<OperationResult<'T>> =
        let key = RepoLocks.canonicalize repo.Value
        RepoLocks.withLockAsync key ct work

    /// Resolve a worktree's HEAD commit. Public for the (rare) caller that
    /// already holds the lock and just wants the SHA.
    let revParseHeadAsync (worktree: RepoPath) (ct: CancellationToken) : Task<Result<CommitId, AveliaError>> =
        task {
            let! r = GitProcess.runAsync worktree.Value [ "rev-parse"; "HEAD" ] ct

            if r.ExitCode = 0 then
                let sha = GitProcess.trimmedStdOut r

                match CommitId.TryCreate sha with
                | Ok c -> return Ok c
                | Error msg -> return Error(AveliaError.External("git", $"rev-parse returned invalid sha: {msg}"))
            else
                return Error(externalErr "git" r)
        }

    interface IGitOperations with

        member _.WorktreeAddAsync(repo: RepoPath, branch: BranchName, worktree: RepoPath, ct: CancellationToken) =
            withRepoLockAsync repo ct (fun () ->
                task {
                    let! addResult = runChecked repo.Value [ "worktree"; "add"; "-b"; branch.Value; worktree.Value ] ct

                    match addResult with
                    | Error e -> return Failure e
                    | Ok _ ->
                        let! headResult = revParseHeadAsync worktree ct

                        match headResult with
                        | Error e -> return Failure e
                        | Ok head ->
                            return
                                Success
                                    { Path = worktree
                                      Branch = branch
                                      Head = head
                                      IsLocked = false }
                })

        member _.WorktreeRemoveAsync(worktree: RepoPath, force: bool, ct: CancellationToken) =
            withWorktreeLockAsync worktree ct (fun () ->
                task {
                    let args = ResizeArray<string>()
                    args.Add "worktree"
                    args.Add "remove"

                    if force then
                        args.Add "--force"

                    args.Add worktree.Value
                    // `git worktree remove` is run from the *main* repo, not the worktree
                    // being removed (the worktree's CWD may have just been deleted).
                    // Resolve the common-dir first to get a stable CWD; fall back to
                    // the worktree path if resolution fails (rare).
                    let! commonDirResult = resolveLockKeyAsync worktree ct

                    let cwd =
                        match commonDirResult with
                        | Ok k -> k
                        | Error _ -> worktree.Value

                    let! r = runChecked cwd args ct

                    match r with
                    | Ok _ ->
                        commonDirCache.TryRemove(RepoLocks.canonicalize worktree.Value) |> ignore
                        return Success()
                    | Error e -> return Failure e
                })

        member _.CommitAsync(worktree: RepoPath, message: CommitMessage, ct: CancellationToken) =
            withWorktreeLockAsync worktree ct (fun () ->
                task {
                    let! commitResult = runChecked worktree.Value [ "commit"; "-m"; message.Value ] ct

                    match commitResult with
                    | Error e -> return Failure e
                    | Ok _ ->
                        let! headResult = revParseHeadAsync worktree ct

                        match headResult with
                        | Ok head -> return Success head
                        | Error e -> return Failure e
                })

        member _.PushAsync(worktree: RepoPath, remote: Remote, ct: CancellationToken) =
            withWorktreeLockAsync worktree ct (fun () ->
                task {
                    let! r = runChecked worktree.Value [ "push"; remote.Value ] ct

                    return
                        match r with
                        | Ok _ -> Success()
                        | Error e -> Failure e
                })

        member _.FetchAsync(worktree: RepoPath, remote: Remote, ct: CancellationToken) =
            withWorktreeLockAsync worktree ct (fun () ->
                task {
                    let! r = runChecked worktree.Value [ "fetch"; remote.Value ] ct

                    return
                        match r with
                        | Ok _ -> Success()
                        | Error e -> Failure e
                })

        member _.CheckoutAsync(worktree: RepoPath, branch: BranchName, ct: CancellationToken) =
            withWorktreeLockAsync worktree ct (fun () ->
                task {
                    let! r = runChecked worktree.Value [ "checkout"; branch.Value ] ct

                    return
                        match r with
                        | Ok _ -> Success()
                        | Error e -> Failure e
                })

        member _.BranchCreateAsync(repo: RepoPath, branch: BranchName, baseRef: BranchName, ct: CancellationToken) =
            withRepoLockAsync repo ct (fun () ->
                task {
                    let! r = runChecked repo.Value [ "branch"; branch.Value; baseRef.Value ] ct

                    return
                        match r with
                        | Ok _ -> Success()
                        | Error e -> Failure e
                })

        member _.BranchDeleteAsync(repo: RepoPath, branch: BranchName, ct: CancellationToken) =
            withRepoLockAsync repo ct (fun () ->
                task {
                    // `-D` (force) so we can drop branches that haven't been
                    // merged upstream — the agent workspace flow regularly
                    // archives unmerged work. Caller's responsibility to
                    // confirm intent at the UI layer.
                    let! r = runChecked repo.Value [ "branch"; "-D"; branch.Value ] ct

                    return
                        match r with
                        | Ok _ -> Success()
                        | Error e -> Failure e
                })
