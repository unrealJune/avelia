namespace Avelia.Vcs.GitHub.Auth

open System
open Avelia.Core.Abstractions

// ============================================================================
//  Auth domain types
//
//  Conventions:
//    * Single-case DUs with <c>.Match</c> visitors for C#.
//    * No <c>'T option</c> in records — empty-string / <c>DateTimeOffset.MaxValue</c>
//      sentinels instead. Mirrors <c>AgentSessionConfig</c> /
//      <c>WorktreeStatus</c> in <c>Avelia.Core.Abstractions</c>.
//    * No exceptions on expected failures; all auth methods return
//      <c>OperationResult&lt;_&gt;</c> with <c>AveliaError</c>.
//
//  Transport-level concerns (HTTP client, response shapes) live in
//  Octokit and stop at <c>Octokit.Internal.IHttpClient</c>. We don't
//  redefine an HTTP boundary on top.
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
        /// Requested OAuth scopes. Empty array = no scopes requested
        /// (GitHub App tokens don't take scopes — their permissions are
        /// pre-set at app install time — and are configured with
        /// <c>[||]</c>).
        Scopes: string array
        /// Which auth method this config drives. Stored on the resulting
        /// token so the shell can render method-specific UI.
        Method: AuthMethod
    }

/// The user-facing half of a device-code challenge. The shell shows
/// <c>UserCode</c> and a clickable <c>VerificationUri</c>; the
/// <c>DeviceCode</c> is the opaque polling key carried back into
/// <c>CompleteDeviceFlowAsync</c>.
type DeviceCodeChallenge =
    {
        /// Short, hyphenated, human-typeable code (GitHub uses
        /// <c>"WDJB-MJHT"</c> shape). Display verbatim.
        UserCode: string
        /// URL the user opens in a browser to enter <c>UserCode</c>.
        /// <c>"https://github.com/login/device"</c> for public GitHub.
        VerificationUri: string
        /// Opaque polling key. Don't show in UI.
        DeviceCode: string
        /// Minimum interval the client should wait between polls. The server
        /// may upgrade this via <c>slow_down</c>; Octokit's polling loop
        /// honours that internally.
        Interval: TimeSpan
        /// When the code stops being polled-acceptable. After this, attempts
        /// surface as <c>Failure (External "github" "expired_token...")</c>.
        ExpiresAt: DateTimeOffset
    }

/// GitHub account login. Single-case DU with a smart constructor so an
/// empty/whitespace login cannot be constructed — anywhere a
/// <see cref="GitHubLogin"/> appears, the value is known non-empty.
/// Mirrors the project-wide pattern (<see cref="Avelia.Core.Abstractions.BranchName"/>,
/// <see cref="Avelia.Core.Abstractions.CommitId"/>, etc.).
[<Struct>]
type GitHubLogin =
    private
    | GitHubLogin of string

    member this.Value =
        let (GitHubLogin s) = this
        if String.IsNullOrEmpty s then "" else s

    override this.ToString() = this.Value

    static member TryCreate(s: string) : Result<GitHubLogin, string> =
        if String.IsNullOrWhiteSpace s then
            Error "GitHub login cannot be null, empty, or whitespace."
        else
            Ok(GitHubLogin(s.Trim()))

    /// Throwing variant for code paths where the input is known valid
    /// (test fixtures, hardcoded design data).
    static member Create(s: string) : GitHubLogin =
        match GitHubLogin.TryCreate s with
        | Ok l -> l
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// A token Octokit handed us before we knew which GitHub account it
/// belonged to. Only emitted by <c>DeviceFlow.completeAsync</c>; the
/// orchestration in <c>Auth.fs</c> immediately resolves the login via
/// <c>GET /user</c> and promotes this to a <see cref="GitHubAccessToken"/>.
/// Never persisted.
///
/// <para>Carries every field except <c>Account</c>: making the
/// post-device-flow state a distinct type rather than a
/// <see cref="GitHubAccessToken"/> with an empty <c>Account</c> means
/// you can't accidentally hand the unresolved token to the credential
/// store — the compiler stops you.</para>
type PendingGitHubToken =
    { Token: string
      Method: AuthMethod
      ScopesGranted: string array
      ExpiresAt: DateTimeOffset
      RefreshToken: string
      RefreshExpiresAt: DateTimeOffset }

/// A successfully-acquired AND login-resolved access token. <c>Account</c>
/// is a <see cref="GitHubLogin"/> so the smart constructor guarantees the
/// credential key (<c>"avelia:github:&lt;login&gt;"</c>) is always
/// well-formed — <c>TokenStore.SaveAsync</c> doesn't need a runtime
/// "is this account empty?" check because the type system already
/// rejected empty values at construction time.
///
/// Stored verbatim in the credential vault; JSON-serialized to keep the
/// metadata travelling with the secret. The serializer round-trip is
/// property-tested.
type GitHubAccessToken =
    {
        /// GitHub login of the authenticated account.
        Account: GitHubLogin
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

[<RequireQualifiedAccess>]
module GitHubAccessToken =
    /// Promote a <see cref="PendingGitHubToken"/> to a full
    /// <see cref="GitHubAccessToken"/> by attaching the resolved login.
    /// The only legal way to build a <see cref="GitHubAccessToken"/>
    /// outside of credential-vault deserialization — every other call
    /// site goes through here.
    let withLogin (login: GitHubLogin) (pending: PendingGitHubToken) : GitHubAccessToken =
        { Account = login
          Token = pending.Token
          Method = pending.Method
          ScopesGranted = pending.ScopesGranted
          ExpiresAt = pending.ExpiresAt
          RefreshToken = pending.RefreshToken
          RefreshExpiresAt = pending.RefreshExpiresAt }
