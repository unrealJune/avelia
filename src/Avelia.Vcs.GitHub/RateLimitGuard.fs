namespace Avelia.Vcs.GitHub

open System
open Avelia.Core.Abstractions

// ============================================================================
//  Rate-limit guard — pure state-machine plus exception mapper
//
//  GitHub's REST API enforces 5000 req/h per token; secondary rate limits
//  apply on top (per-endpoint bursts). The plan calls for backing off
//  below 500 remaining and converting RateLimitExceededException to a
//  structured failure that the polling layer can read.
//
//  Everything here is pure data + functions over <see cref="RateLimitSnapshot"/>.
//  The polling layer (B-12) consults <see cref="classify"/> before each
//  call; the per-call <see cref="captureSnapshot"/> helper updates the
//  shared state from <c>Octokit.GitHubClient.GetLastApiInfo()</c>.
// ============================================================================

[<RequireQualifiedAccess>]
module RateLimitGuard =

    /// Below this many remaining calls, polling layers should back off.
    /// Matches the value called out in <c>docs/plans/backend.md</c>.
    [<Literal>]
    let LowThreshold = 500

    /// Classify a snapshot. Returns <c>Healthy</c> when no snapshot has
    /// been captured yet (an empty snapshot is "we don't know enough to
    /// back off").
    let classify (snapshot: RateLimitSnapshot voption) : RateLimitTier =
        match snapshot with
        | ValueNone -> RateLimitTier.Healthy
        | ValueSome s ->
            if s.Remaining <= 0 then RateLimitTier.Exhausted
            elif s.Remaining < LowThreshold then RateLimitTier.Low
            else RateLimitTier.Healthy

    /// How long to wait before the next call given a snapshot and the
    /// current wall clock. <c>TimeSpan.Zero</c> when the snapshot is
    /// healthy or absent; otherwise the gap to <c>ResetAt</c> (clamped
    /// to zero — never negative).
    let waitUntilOk (snapshot: RateLimitSnapshot voption) (now: DateTimeOffset) : TimeSpan =
        match classify snapshot with
        | RateLimitTier.Healthy -> TimeSpan.Zero
        | RateLimitTier.Low
        | RateLimitTier.Exhausted ->
            // Both states need waiting; Low less aggressively but the
            // caller can pick any policy ≥ this floor.
            match snapshot with
            | ValueNone -> TimeSpan.Zero
            | ValueSome s ->
                let delta = s.ResetAt - now
                if delta < TimeSpan.Zero then TimeSpan.Zero else delta

    /// Build a <see cref="RateLimitSnapshot"/> from Octokit's per-call
    /// <c>ApiInfo.RateLimit</c>. Returns <c>ValueNone</c> when GitHub
    /// didn't include rate-limit headers (test fixtures, GraphQL-only
    /// endpoints) — the polling layer reads that as "no snapshot
    /// available" rather than "rate-limit is zero".
    let fromOctokit (apiInfo: Octokit.ApiInfo) (now: DateTimeOffset) : RateLimitSnapshot voption =
        match box apiInfo with
        | null -> ValueNone
        | _ ->
            let rl = apiInfo.RateLimit

            match box rl with
            | null -> ValueNone
            | _ when rl.Limit = 0 && rl.Remaining = 0 ->
                // GitHub's "no rate limit info on this response" shape.
                // (e.g. /rate_limit itself, login endpoints.) Treat as
                // unknown rather than "exhausted" to avoid spurious
                // backoff on the next call.
                ValueNone
            | _ ->
                ValueSome
                    { Limit = rl.Limit
                      Remaining = rl.Remaining
                      ResetAt = rl.Reset
                      LastUpdated = now }

    /// Map an Octokit rate-limit exception to a structured Avelia error.
    /// The polling layer pattern-matches on this to decide whether to
    /// retry after sleeping vs. propagate to the UI.
    ///
    /// <para>Catches both <c>RateLimitExceededException</c> (5000/h cap)
    /// and <c>SecondaryRateLimitExceededException</c> (per-endpoint burst
    /// guard). Octokit's secondary exception type doesn't expose a typed
    /// <c>Reset</c> member — it inherits from <c>ForbiddenException</c>
    /// and stuffs the detail in <c>Message</c>. The polling layer's
    /// retry budget is the same in both cases (back off for one bucket
    /// reset), so the distinction is logged but not branched on
    /// downstream.</para>
    let exceptionToError (ex: exn) (now: DateTimeOffset) : AveliaError voption =
        match ex with
        | :? Octokit.SecondaryRateLimitExceededException as sec ->
            ValueSome(AveliaError.External("github-ratelimit", $"secondary: {sec.Message}"))
        | :? Octokit.RateLimitExceededException as primary ->
            let resetSecs = max 0 (int (primary.Reset - now).TotalSeconds)
            ValueSome(AveliaError.External("github-ratelimit", $"primary; reset in {resetSecs}s"))
        | _ -> ValueNone
