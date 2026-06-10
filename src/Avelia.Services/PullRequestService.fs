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
        inspection: IGitInspection
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
                        match! repositories.GetAsync(record.Workspace.RepoId, ct) with
                        | Failure e -> return Failure e
                        | Success repo ->
                            match! resolveCoordinate repo ct with
                            | Failure e -> return Failure e
                            | Success coord ->
                                match! getClient ct with
                                | Failure e -> return Failure e
                                | Success client ->
                                    return! client.GetPullRequestAsync(coord, record.Workspace.PrNumber, ct)
            }

        member _.MergeAsync(_id, ct) =
            ct.ThrowIfCancellationRequested()
            // Real merge is a follow-up: PullRequestId carries only a number and
            // IGitHubClient has no merge method yet. Surface a clear, stub-shaped
            // failure rather than silently leaving the merge button inert.
            Task.FromResult(
                (Failure(AveliaError.Conflict "Merging from Avelia isn't wired up yet — merge on github.com."))
                : OperationResult<unit>
            )
