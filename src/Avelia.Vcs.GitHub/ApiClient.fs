namespace Avelia.Vcs.GitHub

open System
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub.Auth
open Octokit.Caching
open Octokit.Internal

// Note: <c>Octokit</c> is intentionally NOT opened. Octokit and Avelia both
// expose <c>PullRequest</c>, <c>Notification</c>, <c>ICredentialStore</c> —
// fully qualifying every Octokit reference here keeps F#'s name resolution
// honest. Aliases below keep the call sites readable.
type private OctokitClient = Octokit.GitHubClient
type private OctokitApiOptions = Octokit.ApiOptions
type private OctokitNewPullRequest = Octokit.NewPullRequest
type private OctokitNotificationsRequest = Octokit.NotificationsRequest
type private OctokitItemState = Octokit.ItemState
type private OctokitApiException = Octokit.ApiException
type private OctokitApiValidationException = Octokit.ApiValidationException
type private OctokitAuthorizationException = Octokit.AuthorizationException
type private OctokitNotFoundException = Octokit.NotFoundException

// ============================================================================
//  IGitHubClient — the GitHub-internal abstraction over Octokit
//
//  Public-but-namespace-scoped so the shell's higher-level services
//  (IRepositoryService / IPullRequestService / IInboxService) can take a
//  dependency at composition time, but no caller imports Octokit types
//  directly.
//
//  ListPrsForUserAsync is left for B-5 (GraphQL batched query — vastly
//  cheaper than the REST equivalent of 76 round-trips).
// ============================================================================

type IGitHubClient =
    /// Repos the authenticated user has explicit access to. Returns
    /// **at most** <see cref="GitHubClient.MaxReposReturned"/> entries
    /// (the most recently updated). Accounts with more repos than that
    /// (~1000 for the default cap) get a truncated result — the shell
    /// is expected to surface a "show more" affordance backed by a
    /// search/filter API, not to enumerate every repo. Capping here
    /// keeps the call bounded so we don't blow ~50 rate-limit quota in
    /// one shot.
    abstract ListUserReposAsync: CancellationToken -> Task<OperationResult<IReadOnlyList<RepoSummary>>>

    abstract GetPullRequestAsync:
        repo: RepoCoordinate * prNumber: int * CancellationToken -> Task<OperationResult<PullRequest>>

    abstract CreatePullRequestAsync: request: CreatePrRequest * CancellationToken -> Task<OperationResult<PullRequest>>

    /// Post a comment to a pull request's conversation thread.
    abstract CommentAsync:
        repo: RepoCoordinate * prNumber: int * body: string * CancellationToken -> Task<OperationResult<unit>>

    /// Notifications updated after <paramref name="since"/>. Returns
    /// **at most** <see cref="GitHubClient.MaxNotificationsReturned"/>
    /// entries (the most recent). Sized for the inbox surface; older
    /// notifications are unlikely to be actionable and pulling them all
    /// would waste rate-limit budget on idle accounts.
    ///
    /// <para><c>since = DateTimeOffset.MinValue</c> ⇒ "everything";
    /// matches the project-wide empty-sentinel convention.</para>
    abstract ListNotificationsAsync:
        since: DateTimeOffset * CancellationToken -> Task<OperationResult<IReadOnlyList<Notification>>>

    /// Last rate-limit snapshot observed by this client. Polling layers
    /// consult this before scheduling the next tick.
    abstract LastRateLimit: RateLimitSnapshot voption

// ============================================================================
//  Octokit-backed implementation
//
//  Construction is split into three layers so each piece is independently
//  testable:
//    * <see cref="OctokitInfra.OctokitFactory.buildConnection"/> — wires
//      a single Octokit Connection given a base address, credentials, and
//      <see cref="IHttpClient"/>.
//    * <see cref="GitHubClient"/> ctor — accepts a pre-built
//      <see cref="Octokit.GitHubClient"/>. Test path: pass a client built
//      with a scripted <see cref="IHttpClient"/>.
//    * <see cref="GitHubClient.CreateAsync"/> — production convenience;
//      loads the bearer token via <see cref="IGitHubAuth"/>, wires the
//      caching layer, and hands back a ready-to-use client.
// ============================================================================

/// Concrete <see cref="IGitHubClient"/> impl wrapping Octokit.
///
/// <para>Pagination caps: hard limits on how many entries each
/// list method walks before returning. Bounded so a single call never
/// blows more than ~10 of the rate-limit budget (5000/h).</para>
///
/// <para>Rate-limit enforcement: before every Octokit call,
/// <see cref="invoke"/> consults <see cref="lastRateLimit"/> via
/// <see cref="RateLimitGuard.classify"/>. If the most recent snapshot is
/// <c>Exhausted</c>, the call is short-circuited with
/// <c>Failure (External "github-ratelimit", ...)</c> carrying the wait
/// time — the caller (polling layer) is expected to sleep
/// <c>waitUntilOk</c> before retrying. <c>Low</c> snapshots proceed but
/// are visible to the polling layer via <see cref="LastRateLimit"/> so
/// it can lengthen its tick. <c>Healthy</c> snapshots proceed
/// unconditionally.</para>
///
/// <para>This makes the guard part of the call path rather than a
/// passive observer; callers that walk <c>RateLimitGuard.waitUntilOk</c>
/// themselves before each call get redundant protection, not contradiction.</para>
type GitHubClient(client: OctokitClient, clock: Func<DateTimeOffset>, responseCache: IResponseCache) =

    /// Last snapshot captured from <c>client.GetLastApiInfo().RateLimit</c>.
    /// F# field assignments on heap references are atomic on .NET, so
    /// concurrent polling-layer reads see either the previous or the new
    /// snapshot without tearing.
    let mutable lastRateLimit: RateLimitSnapshot voption = ValueNone

    /// Capture the most recent rate-limit snapshot after a call (or a
    /// call attempt that produced response headers).
    let captureRateLimit () =
        try
            let info = client.GetLastApiInfo()

            match RateLimitGuard.fromOctokit info (clock.Invoke()) with
            | ValueSome s -> lastRateLimit <- ValueSome s
            | ValueNone -> ()
        with _ ->
            // GetLastApiInfo() throws if no call has been made yet.
            // Leave lastRateLimit untouched.
            ()

    /// Map Octokit exceptions to our error vocabulary. Rate-limit
    /// exceptions get the dedicated <c>github-ratelimit</c> source so
    /// the polling layer can branch on it without parsing a free-form
    /// message.
    let mapException (ex: exn) : AveliaError =
        match RateLimitGuard.exceptionToError ex (clock.Invoke()) with
        | ValueSome err -> err
        | ValueNone ->
            match ex with
            | :? OctokitAuthorizationException -> AveliaError.Unauthorized
            | :? OctokitNotFoundException as nf -> AveliaError.NotFound nf.Message
            | :? OctokitApiValidationException as v -> AveliaError.Validation v.Message
            | :? OctokitApiException as api ->
                AveliaError.External("github", $"HTTP {int api.StatusCode}: {api.Message}")
            | :? System.Net.Http.HttpRequestException as net -> AveliaError.Network net.Message
            | _ -> AveliaError.External("github", ex.Message)

    /// Check the cached rate-limit snapshot. Returns <c>ValueSome err</c>
    /// to short-circuit when exhausted (carrying the wait-time in
    /// seconds for the caller); <c>ValueNone</c> when it's safe to
    /// proceed.
    ///
    /// <para>Only <c>Exhausted</c> blocks. <c>Low</c> still proceeds —
    /// the snapshot is advisory and the polling layer should slow down
    /// based on <see cref="LastRateLimit"/>, but a single in-flight
    /// request shouldn't be denied service for crossing the warning
    /// threshold.</para>
    let preflightRateLimit () : AveliaError voption =
        match RateLimitGuard.classify lastRateLimit with
        | RateLimitTier.Exhausted ->
            let wait = RateLimitGuard.waitUntilOk lastRateLimit (clock.Invoke())
            let secs = max 0 (int wait.TotalSeconds)

            ValueSome(AveliaError.External("github-ratelimit", $"preflight; reset in {secs}s"))
        | RateLimitTier.Low
        | RateLimitTier.Healthy -> ValueNone

    /// Run an Octokit call and translate the outcome into
    /// <see cref="OperationResult"/>. Pre-flights the rate-limit guard;
    /// cancellation propagates out as <see cref="OperationCanceledException"/>
    /// via <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>
    /// — we can't <c>reraise()</c> from inside a task computation
    /// expression (F# compiler restricts <c>reraise</c> to direct
    /// <c>with</c> handlers).
    let invoke (f: unit -> Task<'T>) : Task<OperationResult<'T>> =
        task {
            match preflightRateLimit () with
            | ValueSome err -> return Failure err
            | ValueNone ->
                let mutable cancelDispatch: System.Runtime.ExceptionServices.ExceptionDispatchInfo | null =
                    null

                let mutable result: OperationResult<'T> = Failure(AveliaError.Internal "unfilled")

                try
                    try
                        let! r = f ()
                        result <- Success r
                    with
                    | :? OperationCanceledException as oce ->
                        cancelDispatch <- System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture oce
                    | ex -> result <- Failure(mapException ex)
                finally
                    captureRateLimit ()

                match cancelDispatch with
                | null -> return result
                | dispatch ->
                    dispatch.Throw()
                    // Unreachable — Throw() never returns.
                    return result
        }

    /// Map an Octokit <c>Repository</c> to our <see cref="RepoSummary"/>.
    /// Falls back to sane sentinels when GitHub returns empty fields
    /// (uninitialised repos lack a default branch).
    let toRepoSummary (r: Octokit.Repository) : RepoSummary =
        let defaultBranch =
            if String.IsNullOrEmpty r.DefaultBranch then
                Unchecked.defaultof<BranchName>
            else
                match BranchName.TryCreate r.DefaultBranch with
                | Ok b -> b
                | Error _ -> Unchecked.defaultof<BranchName>

        { Owner = if isNull r.Owner then "" else r.Owner.Login
          Name = r.Name
          DefaultBranch = defaultBranch
          IsPrivate = r.Private
          CloneUrl = if isNull r.CloneUrl then "" else r.CloneUrl }

    let toPrStatus (pr: Octokit.PullRequest) : PrStatus =
        if pr.Merged then
            PrStatus.Merged
        elif pr.State.Value = OctokitItemState.Closed then
            PrStatus.Closed
        elif pr.Draft then
            PrStatus.Draft
        else
            PrStatus.Open

    let toPullRequest (pr: Octokit.PullRequest) : Avelia.Core.Abstractions.PullRequest =
        let branch =
            match BranchName.TryCreate(if isNull pr.Head then "" else pr.Head.Ref) with
            | Ok b -> b
            | Error _ -> Unchecked.defaultof<BranchName>

        let baseRef =
            match BranchName.TryCreate(if isNull pr.Base then "" else pr.Base.Ref) with
            | Ok b -> b
            | Error _ -> Unchecked.defaultof<BranchName>

        { Id = PullRequestId pr.Number
          Number = pr.Number
          Title = if isNull pr.Title then "" else pr.Title
          Branch = branch
          Base = baseRef
          Status = toPrStatus pr
          Checks = Array.empty
          MergeReady = pr.Mergeable.GetValueOrDefault false }

    let toNotification (n: Octokit.Notification) : Avelia.Vcs.GitHub.Notification =
        // GitHub's notifications API returns UpdatedAt as an ISO-8601
        // string (the wire format), Octokit doesn't parse it for us.
        let updated =
            match DateTimeOffset.TryParse(if isNull n.UpdatedAt then "" else n.UpdatedAt) with
            | true, dto -> dto
            | _ -> DateTimeOffset.MinValue

        { Id = if isNull n.Id then "" else n.Id
          RepoFullName = if isNull n.Repository then "" else n.Repository.FullName
          Subject =
            if isNull n.Subject then ""
            elif isNull n.Subject.Title then ""
            else n.Subject.Title
          Reason = if isNull n.Reason then "" else n.Reason
          UpdatedAt = updated }

    // -------------------------------------------------------------------
    //  Convenience constructors + capacity knobs
    // -------------------------------------------------------------------

    /// Hard upper bound on entries returned by <c>ListUserReposAsync</c>.
    /// Per-page size is 100 (GitHub's max), so this is ~10 paginated
    /// round-trips. Accounts with more repos truncate (and the shell
    /// pushes them to search-filter UX, not raw enumeration).
    static member val MaxReposReturned = 1000 with get

    /// Hard upper bound on entries returned by <c>ListNotificationsAsync</c>.
    /// Per-page size is 50 (Octokit's default for this endpoint), so
    /// this is ~10 round-trips. Older notifications are unlikely to
    /// matter for inbox UX.
    static member val MaxNotificationsReturned = 500 with get

    /// Production convenience. Loads the bearer token for
    /// <paramref name="login"/> via <paramref name="auth"/>, builds an
    /// Octokit client against <c>api.github.com</c> wrapped in our
    /// ETag-aware <see cref="Octokit.Caching.CachingHttpClient"/>, and
    /// returns a ready-to-use <see cref="GitHubClient"/>.
    ///
    /// <para>The returned client owns its <see cref="IResponseCache"/>
    /// (in-memory, process-scoped); pass a long-lived instance into
    /// composition rather than constructing per-call.</para>
    static member CreateAsync
        (auth: Auth.IGitHubAuth, login: Auth.GitHubLogin, ct: CancellationToken)
        : Task<OperationResult<GitHubClient>> =
        task {
            let! tokenResult = auth.LoadStoredTokenAsync(login, ct)

            match tokenResult with
            | Failure e -> return Failure e
            | Success token ->
                let baseAddress = OctokitFactory.defaultApiBaseAddress
                let credentials = StaticOctokitCredentials token.Token :> Octokit.ICredentialStore
                let innerHttp = OctokitFactory.defaultHttpClientFactory.Invoke()
                let cache = InMemoryResponseCache() :> IResponseCache
                let cachingHttp = new CachingHttpClient(innerHttp, cache) :> IHttpClient
                let octokit = OctokitFactory.buildClient baseAddress credentials cachingHttp
                let clock = Func<DateTimeOffset>(fun () -> DateTimeOffset.UtcNow)
                return Success(GitHubClient(octokit, clock, cache))
        }

    /// Test constructor — accept a fully-built Octokit client + cache.
    /// Bypasses credential resolution so unit tests can wire a stubbed
    /// <see cref="IHttpClient"/> straight into the Connection.
    new(client: OctokitClient, responseCache: IResponseCache) =
        GitHubClient(client, Func<DateTimeOffset>(fun () -> DateTimeOffset.UtcNow), responseCache)

    member _.ResponseCache = responseCache

    interface IGitHubClient with
        member _.LastRateLimit = lastRateLimit

        member _.ListUserReposAsync(ct: CancellationToken) =
            task {
                // 100 entries per page (GitHub's max for /user/repos);
                // cap pages so we don't blow more than ~10 of the
                // rate-limit budget in one call.
                let pageSize = 100
                let pageCount = max 1 (GitHubClient.MaxReposReturned / pageSize)
                let apiOptions = OctokitApiOptions(PageSize = pageSize, PageCount = pageCount)
                let! result = invoke (fun () -> client.Repository.GetAllForCurrent apiOptions)

                return
                    match result with
                    | Failure e -> Failure e
                    | Success repos ->
                        let mapped =
                            repos
                            |> Seq.filter (fun r -> not (isNull r))
                            |> Seq.map toRepoSummary
                            |> Seq.toArray

                        Success(mapped :> IReadOnlyList<_>)
            }

        member _.GetPullRequestAsync(repo: RepoCoordinate, prNumber: int, ct: CancellationToken) =
            task {
                let! result = invoke (fun () -> client.PullRequest.Get(repo.Owner, repo.Name, prNumber))

                return
                    match result with
                    | Failure e -> Failure e
                    | Success pr -> Success(toPullRequest pr)
            }

        member _.CreatePullRequestAsync(request: CreatePrRequest, ct: CancellationToken) =
            task {
                let np =
                    OctokitNewPullRequest(request.Title, request.Head.Value, request.Base.Value)

                np.Body <- request.Body
                np.Draft <- System.Nullable request.Draft

                let! result = invoke (fun () -> client.PullRequest.Create(request.Repo.Owner, request.Repo.Name, np))

                return
                    match result with
                    | Failure e -> Failure e
                    | Success pr -> Success(toPullRequest pr)
            }

        member _.CommentAsync(repo: RepoCoordinate, prNumber: int, body: string, ct: CancellationToken) =
            task {
                let safeBody =
                    match box body with
                    | null -> ""
                    | _ -> body

                let! result = invoke (fun () -> client.Issue.Comment.Create(repo.Owner, repo.Name, prNumber, safeBody))

                return
                    match result with
                    | Failure e -> Failure e
                    | Success _ -> Success()
            }

        member _.ListNotificationsAsync(since: DateTimeOffset, ct: CancellationToken) =
            task {
                let req = OctokitNotificationsRequest()

                if since <> DateTimeOffset.MinValue then
                    req.Since <- System.Nullable since

                let pageSize = 50
                let pageCount = max 1 (GitHubClient.MaxNotificationsReturned / pageSize)
                let apiOptions = OctokitApiOptions(PageSize = pageSize, PageCount = pageCount)
                let! result = invoke (fun () -> client.Activity.Notifications.GetAllForCurrent(req, apiOptions))

                return
                    match result with
                    | Failure e -> Failure e
                    | Success notifications ->
                        let mapped =
                            notifications
                            |> Seq.filter (fun n -> not (isNull n))
                            |> Seq.map toNotification
                            |> Seq.toArray

                        Success(mapped :> IReadOnlyList<_>)
            }
