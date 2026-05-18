namespace Avelia.Vcs.GitHub.Auth

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

// ============================================================================
//  Auth domain types
//
//  These cross the boundary into the shell (onboarding UI, settings) so they
//  follow the project's C#-friendly conventions:
//    * Single-case DUs with <c>.Match</c> visitors.
//    * No <c>'T option</c> in records — empty-string / <c>DateTimeOffset.MaxValue</c>
//      sentinels instead. Mirrors <c>AgentSessionConfig</c> /
//      <c>WorktreeStatus</c> in <c>Avelia.Core.Abstractions</c>.
//    * No exceptions on expected failures; all auth methods return
//      <c>OperationResult&lt;_&gt;</c> with <c>AveliaError</c>.
// ============================================================================

/// Which authentication path produced a stored token. The shell renders these
/// differently in settings (e.g. "Sign out" for GitHub App; "Replace PAT" for
/// PAT). Future flows (e.g. SSH-cert) become new cases.
[<RequireQualifiedAccess>]
type AuthMethod =
    /// GitHub App + OAuth Device Flow — primary path. User-to-server tokens
    /// with short lifetime + refresh.
    | GitHubApp
    /// OAuth App + Device Flow — fallback for enterprises that disallow
    /// GitHub Apps. Same protocol, different client id and scopes.
    | OAuthApp
    /// Personal Access Token entered by the user. Classic or fine-grained.
    | Pat

    /// Visitor over the union — keeps C# off the F# DU's nested case types.
    /// Mirrors the project-wide <c>.Match</c> convention.
    member this.Match<'TResult>
        (gitHubApp: System.Func<'TResult>, oauthApp: System.Func<'TResult>, pat: System.Func<'TResult>)
        : 'TResult =
        match this with
        | GitHubApp -> gitHubApp.Invoke()
        | OAuthApp -> oauthApp.Invoke()
        | Pat -> pat.Invoke()

/// Configuration for one of the device-flow paths. <c>Host</c> is the GitHub
/// origin — <c>"https://github.com"</c> for the public service,
/// <c>"https://my-ghe.example"</c> for GHES installs. <c>ClientId</c> is the
/// app id GitHub assigned; <c>Scopes</c> is the requested OAuth scope set.
type DeviceFlowConfig =
    {
        Host: string
        ClientId: string
        /// Requested OAuth scopes. Empty array = no scopes requested (the
        /// GitHub App path doesn't take scopes — its permissions are pre-set
        /// at app install time — and is configured with <c>[||]</c>).
        Scopes: string array
        /// Which auth method this config drives. Stored on the resulting
        /// token so the shell can render method-specific UI.
        Method: AuthMethod
    }

/// The user-facing half of a device-code challenge. The shell shows
/// <c>UserCode</c> and a clickable <c>VerificationUri</c>; the
/// <c>DeviceCode</c> is the opaque polling key that we keep server-side of
/// the UI.
type DeviceCodeChallenge =
    {
        /// Short, hyphenated, human-typeable code (GitHub uses
        /// <c>"WDJB-MJHT"</c> shape). Display verbatim.
        UserCode: string
        /// URL the user opens in a browser to enter <c>UserCode</c>.
        /// <c>"https://github.com/login/device"</c> for public GitHub.
        VerificationUri: string
        /// Optional pre-filled completion URL — GitHub also returns
        /// <c>verification_uri_complete</c>. Empty string when not supplied;
        /// per project convention we never use <c>'T option</c> at the
        /// boundary.
        VerificationUriComplete: string
        /// Opaque polling key. Don't show in UI.
        DeviceCode: string
        /// Minimum interval the client should wait between polls. The server
        /// may upgrade this via <c>slow_down</c>; clients must obey.
        Interval: TimeSpan
        /// When the code stops being polled-acceptable. After this, attempts
        /// surface as <c>Failure (Validation "expired")</c>.
        ExpiresAt: DateTimeOffset
    }

/// A successfully-acquired access token. <c>Account</c> is the GitHub login
/// (<c>""</c> when the caller hasn't resolved it yet — e.g. immediately after
/// device-flow completion). <c>Token</c> is the raw bearer string.
///
/// Stored verbatim in the credential vault; JSON-serialized to keep the
/// metadata travelling with the secret. The serializer round-trip is
/// property-tested.
type GitHubAccessToken =
    {
        /// GitHub login of the authenticated account. <c>""</c> means
        /// "not yet resolved" — the shell calls <c>GET /user</c> to populate
        /// it before storing.
        Account: string
        /// Bearer token. Never log.
        Token: string
        Method: AuthMethod
        /// Scopes the server granted (may differ from those requested).
        /// Empty for GitHub App tokens (whose permissions are app-defined,
        /// not scope-defined).
        ScopesGranted: string array
        /// Expiry. <c>DateTimeOffset.MaxValue</c> = never expires (PATs
        /// without a configured expiry; user-to-server tokens always carry a
        /// real value).
        ExpiresAt: DateTimeOffset
        /// Refresh token for renewing user-to-server access without a new
        /// device-flow round-trip. <c>""</c> when not issued (PATs;
        /// device-flow installs that didn't grant a refresh).
        RefreshToken: string
        /// When the refresh token itself stops working.
        /// <c>DateTimeOffset.MaxValue</c> when no refresh token is present
        /// or when the server didn't supply an expiry.
        RefreshExpiresAt: DateTimeOffset
    }

// ============================================================================
//  HTTP transport boundary
//
//  Hand-rolled because Octokit doesn't give us a Device-Flow surface and
//  pulling it in just for that is wasted dependency. The boundary is small
//  (one method) so we can stub it in tests without going to the network.
//
//  Production impl is a thin wrapper around <see cref="HttpClient"/> — see
//  <c>HttpTransport</c> in <c>DeviceFlow.fs</c>.
// ============================================================================

/// Minimal HTTP response shape we need for the device-flow + PAT-validation
/// paths. Body is held as a UTF-8 string (response sizes are tiny — a token
/// blob is &lt;1 KB).
type HttpResponse =
    {
        StatusCode: int
        Body: string
        /// Selected headers we care about (<c>Content-Type</c>,
        /// <c>X-RateLimit-Remaining</c>, etc.). Stored as a
        /// case-insensitive dictionary so callers don't have to remember the
        /// server's exact casing.
        Headers: IReadOnlyDictionary<string, string>
    }

/// Boundary the device-flow + PAT code calls. One method covers GET/POST
/// because we never need streaming and never upload more than a form body.
///
/// Implementations MUST:
/// - Set the <c>Accept</c> header on the request (we want JSON not the
///   default <c>x-www-form-urlencoded</c> response).
/// - Honour <paramref name="cancellationToken"/> — slow networks are real.
/// - Surface transport errors as <c>OperationResult</c> failures, not
///   exceptions, so the caller's <c>match</c> stays total.
type IHttpTransport =
    /// Send a single request. <paramref name="method"/> is the HTTP method
    /// verb (<c>"GET"</c>, <c>"POST"</c>). <paramref name="bearerToken"/> is
    /// <c>""</c> for unauthenticated requests.
    ///
    /// <para><paramref name="formBody"/> is a sequence of
    /// <c>(name, value)</c> tuples; when non-empty it's encoded as
    /// <c>application/x-www-form-urlencoded</c>. Empty sequence = no body.</para>
    abstract SendAsync:
        method: string *
        url: string *
        bearerToken: string *
        formBody: seq<string * string> *
        cancellationToken: CancellationToken ->
            Task<OperationResult<HttpResponse>>
