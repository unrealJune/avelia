namespace Avelia.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub

/// Real <c>IPullRequestService</c> backed by the Octokit client (B-4/B-5).
///
/// A workspace records only a PR <em>number</em>; this service resolves it
/// against the repo's <c>origin</c> owner/name coordinate (derived from the
/// configured remote URL and cached per repo). A workspace with no PR
/// (<c>PrNumber = 0</c>) returns <c>NotFound</c> — the benign no-PR contract
/// the PR pane treats as "no header", not an error.
///
/// <paramref name="getClient"/> resolves the GitHub client lazily (composition
/// passes <c>GitHubClientProvider.GetAsync</c>); tests pass a function over a
/// scripted <c>IGitHubClient</c>.
type PullRequestService
    (
        getClient: CancellationToken -> Task<OperationResult<IGitHubClient>>,
        workspaces: IWorkspaceStore,
        repositories: IRepositoryStore,
        inspection: IGitInspection,
        gitOps: IGitOperations
    ) =

    // owner/name is stable for a repo's origin; resolve once, then cache.
    let coordinateCache = ConcurrentDictionary<RepositoryId, RepoCoordinate>()

    /// Parse the <c>owner/name</c> coordinate out of a git remote URL — scp-like
    /// (<c>git@github.com:owner/name.git</c>), <c>ssh://</c>, or <c>https</c>
    /// forms — by taking the last two path segments and dropping a trailing
    /// <c>.git</c>.
    let parseCoordinate (url: string) : RepoCoordinate option =
        if String.IsNullOrWhiteSpace url then
            None
        else
            let trimmed = url.Trim()

            let withoutGit =
                if trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) then
                    trimmed.Substring(0, trimmed.Length - 4)
                else
                    trimmed

            let segments =
                withoutGit.Split([| '/'; ':' |], StringSplitOptions.RemoveEmptyEntries)

            if segments.Length >= 2 then
                let name = segments.[segments.Length - 1]
                let owner = segments.[segments.Length - 2]

                if String.IsNullOrWhiteSpace owner || String.IsNullOrWhiteSpace name then
                    None
                else
                    Some { Owner = owner; Name = name }
            else
                None

    let resolveCoordinate (repo: Repository) (ct: CancellationToken) : Task<OperationResult<RepoCoordinate>> =
        task {
            match coordinateCache.TryGetValue repo.Id with
            | true, coord -> return Success coord
            | _ ->
                match! inspection.GetRemoteUrlAsync(repo.Path, Remote.Origin, ct) with
                | Failure e -> return Failure e
                | Success url ->
                    match parseCoordinate url with
                    | Some coord ->
                        coordinateCache.[repo.Id] <- coord
                        return Success coord
                    | None ->
                        return
                            Failure(
                                AveliaError.External(
                                    "github",
                                    sprintf "Could not parse owner/name from remote '%s'." url
                                )
                            )
        }

    /// Resolve the GitHub coordinate + client for a workspace's repo. Shared by
    /// the get / create / merge paths.
    let resolveRepoContext
        (record: WorkspaceRecord)
        (ct: CancellationToken)
        : Task<OperationResult<RepoCoordinate * IGitHubClient>> =
        task {
            match! repositories.GetAsync(record.Workspace.RepoId, ct) with
            | Failure e -> return Failure e
            | Success repo ->
                match! resolveCoordinate repo ct with
                | Failure e -> return Failure e
                | Success coord ->
                    match! getClient ct with
                    | Failure e -> return Failure e
                    | Success client -> return Success(coord, client)
        }

    interface IPullRequestService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            task {
                match! workspaces.GetAsync(workspaceId, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    if record.Workspace.PrNumber = 0 then
                        // No PR yet — the pane renders the empty state, not an error.
                        return Failure(AveliaError.NotFound(sprintf "pull-request-for-workspace:%O" workspaceId))
                    else
                        match! resolveRepoContext record ct with
                        | Failure e -> return Failure e
                        | Success(coord, client) ->
                            return! client.GetPullRequestAsync(coord, record.Workspace.PrNumber, ct)
            }

        member _.CreateForWorkspaceAsync(workspaceId, title, body, draft, ct) =
            task {
                let safeTitle = if isNull (box title) then "" else title.Trim()
                let safeBody = if isNull (box body) then "" else body

                if String.IsNullOrWhiteSpace safeTitle then
                    return Failure(AveliaError.Validation "A pull-request title is required.")
                else
                    match! workspaces.GetAsync(workspaceId, ct) with
                    | Failure e -> return Failure e
                    | Success record ->
                        if record.Workspace.PrNumber <> 0 then
                            return
                                Failure(
                                    AveliaError.Conflict(
                                        sprintf "This workspace already has PR #%d." record.Workspace.PrNumber
                                    )
                                )
                        else
                            match! resolveRepoContext record ct with
                            | Failure e -> return Failure e
                            | Success(coord, client) ->
                                // A PR can't reference an unpushed branch — push
                                // the worktree's branch to origin first.
                                match! gitOps.PushAsync(record.WorktreePath, Remote.Origin, ct) with
                                | Failure e -> return Failure e
                                | Success() ->
                                    let request =
                                        { Repo = coord
                                          Title = safeTitle
                                          Body = safeBody
                                          Head = record.Workspace.Branch
                                          Base = record.Workspace.Base
                                          Draft = draft }

                                    match! client.CreatePullRequestAsync(request, ct) with
                                    | Failure e -> return Failure e
                                    | Success pr ->
                                        // Record the new number on the workspace so
                                        // subsequent loads resolve it as a live PR.
                                        let updated =
                                            { record with
                                                Workspace =
                                                    { record.Workspace with
                                                        PrNumber = pr.Number } }

                                        match! workspaces.UpsertAsync(updated, ct) with
                                        | Failure e -> return Failure e
                                        | Success() -> return Success pr
            }

        member _.MergeForWorkspaceAsync(workspaceId, method, ct) =
            task {
                match! workspaces.GetAsync(workspaceId, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    if record.Workspace.PrNumber = 0 then
                        return Failure(AveliaError.NotFound(sprintf "pull-request-for-workspace:%O" workspaceId))
                    else
                        match! resolveRepoContext record ct with
                        | Failure e -> return Failure e
                        | Success(coord, client) ->
                            match! client.MergePullRequestAsync(coord, record.Workspace.PrNumber, method, ct) with
                            | Failure e -> return Failure e
                            | Success() ->
                                // The work is now merged: settle the workspace back
                                // to Ready so the "unmerged work" indicator clears
                                // and survives a restart. Best-effort — a persist
                                // failure must not mask a successful merge.
                                let merged =
                                    { record with
                                        Workspace =
                                            { record.Workspace with
                                                Status = WorkspaceStatus.Ready } }

                                let! _ = workspaces.UpsertAsync(merged, ct)
                                return Success()
            }
