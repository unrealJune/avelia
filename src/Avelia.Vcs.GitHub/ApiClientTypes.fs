namespace Avelia.Vcs.GitHub

open System
open Avelia.Core.Abstractions

// ============================================================================
//  Public DTOs returned by IGitHubClient
//
//  Stay in the project's primitive-obsession-free style: BranchName /
//  RepoCoordinate / typed IDs cross the boundary, not raw strings. Records
//  use the project's empty-sentinel convention — no 'T option in fields.
//
//  These mirror (a subset of) Octokit's data types but stripped to the
//  shape the rest of Avelia needs. The mapping happens at the boundary in
//  ApiClient.fs so the rest of the codebase never imports Octokit.
// ============================================================================

/// Summary of a repository as returned by the "list repos" endpoint.
/// The shell uses this for the repo picker in onboarding (B-12); the
/// existing <see cref="Avelia.Core.Abstractions.Repository"/> record is
/// the local on-disk shape and carries a <c>RepoPath</c> that doesn't
/// exist server-side, so we don't reuse it here.
type RepoSummary =
    {
        /// Owner login (user or org). Lowercased to match the API
        /// response shape — GitHub URLs are case-insensitive but the
        /// canonical form is lowercase.
        Owner: string
        Name: string
        /// The repo's default branch (typically <c>main</c>). Empty
        /// when GitHub didn't return one (uninitialised repo); the
        /// caller should treat that as "no PRs possible yet".
        DefaultBranch: BranchName
        IsPrivate: bool
        /// Full clone URL for the repo. The shell uses this when the
        /// user picks a repo to clone locally; empty when unavailable.
        CloneUrl: string
    }

/// Body of <c>POST /repos/{owner}/{repo}/pulls</c>.
type CreatePrRequest =
    {
        Repo: RepoCoordinate
        Title: string
        Body: string
        Head: BranchName
        Base: BranchName
        /// True to open as a draft PR. Draft PRs don't trigger required
        /// reviewers and skip notifications until marked ready.
        Draft: bool
    }

/// Subset of <c>GET /notifications</c> the inbox surface needs.
/// <c>Reason</c> mirrors GitHub's vocabulary verbatim (e.g.
/// <c>"subject_merged"</c>, <c>"review_requested"</c>) so the inbox
/// classifier can pattern-match without re-mapping.
type Notification =
    {
        /// GitHub's notification thread id; opaque to us, used only as
        /// a stable key for de-duplication.
        Id: string
        /// <c>"owner/repo"</c> — the upstream representation. Easier to
        /// keep raw than to round-trip through <see cref="RepoCoordinate"/>
        /// since GitHub uses this form throughout the notifications
        /// surface.
        RepoFullName: string
        /// Subject summary GitHub provides (PR title, issue title).
        Subject: string
        Reason: string
        UpdatedAt: DateTimeOffset
    }

// ============================================================================
//  Rate-limit snapshot — pure data type, lives next to RateLimitGuard
//
//  Carried out of every successful response so the polling layer can
//  decide whether to back off before hitting RateLimitExceededException.
//  Mirrors Octokit's RateLimit shape but is owned by us (so future
//  non-Octokit backends slot in without rewriting the polling layer).
// ============================================================================

/// Coarse classification used by the polling layer.
[<RequireQualifiedAccess>]
type RateLimitTier =
    /// > 500 remaining — proceed.
    | Healthy
    /// Between the warning and the hard cap — back off polling
    /// intervals; serve cached data preferentially.
    | Low
    /// Zero remaining — only resume after the snapshot's <c>ResetAt</c>.
    | Exhausted

    member this.Match<'TResult>
        (healthy: System.Func<'TResult>, low: System.Func<'TResult>, exhausted: System.Func<'TResult>)
        : 'TResult =
        match this with
        | Healthy -> healthy.Invoke()
        | Low -> low.Invoke()
        | Exhausted -> exhausted.Invoke()

/// Snapshot of a rate-limit response from GitHub. Captured after every
/// API call from <c>client.GetLastApiInfo().RateLimit</c>.
///
/// <para><c>LastUpdated</c> is the local time the snapshot was taken (not
/// the server-side "reset" time) so a stale snapshot can be detected when
/// the resetAt has passed but no fresh call has been made yet.</para>
type RateLimitSnapshot =
    { Limit: int
      Remaining: int
      ResetAt: DateTimeOffset
      LastUpdated: DateTimeOffset }
