namespace Avelia.Vcs.GitHub.Auth

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Meziantou.Framework.Win32

// ============================================================================
//  Public auth surface — composed orchestration over DeviceFlow + TokenStore
//
//  The shell talks to this interface only. Device-flow and credential-store
//  implementations remain free to evolve without breaking the shell binding.
// ============================================================================

/// Public surface for GitHub authentication. Composed by the shell-side
/// onboarding flow (B-12) and consumed by the rest of the GitHub VCS layer
/// (Octokit client, polling loops) to obtain a token at request time.
///
/// <para>Lifecycle is owned by composition — one <see cref="IGitHubAuth"/>
/// per process, holding the shared HTTP transport and credential store
/// instance.</para>
type IGitHubAuth =
    /// Step 1 of the device-flow handshake. Fetches a challenge from
    /// GitHub. The caller displays <see cref="DeviceCodeChallenge.UserCode"/>
    /// and <see cref="DeviceCodeChallenge.VerificationUri"/> to the user
    /// and then awaits <see cref="CompleteDeviceFlowAsync"/>.
    abstract BeginDeviceFlowAsync:
        config: DeviceFlowConfig * CancellationToken -> Task<OperationResult<DeviceCodeChallenge>>

    /// Step 2 of the device-flow handshake. Polls until the user approves
    /// or the code expires, then resolves the login and persists the
    /// resulting token in the credential store under
    /// <c>"avelia:github:&lt;login&gt;"</c>.
    abstract CompleteDeviceFlowAsync:
        config: DeviceFlowConfig * challenge: DeviceCodeChallenge * CancellationToken ->
            Task<OperationResult<GitHubAccessToken>>

    /// Validate a PAT (any host this auth instance can reach) by calling
    /// <c>GET /user</c>. On success, persists the token under the
    /// resolved login. Wrong-PAT input surfaces as
    /// <c>Failure (AveliaError.Unauthorized)</c>; transport errors as
    /// <c>Failure (AveliaError.Network ...)</c>.
    abstract SignInWithPatAsync:
        config: DeviceFlowConfig * pat: string * CancellationToken -> Task<OperationResult<GitHubAccessToken>>

    /// Load a previously-stored token for <paramref name="login"/>. Used by
    /// the Octokit client to attach the bearer header on every request.
    abstract LoadStoredTokenAsync: login: string * CancellationToken -> Task<OperationResult<GitHubAccessToken>>

    /// Remove the stored token for <paramref name="login"/>. Idempotent.
    abstract SignOutAsync: login: string * CancellationToken -> Task<OperationResult<unit>>

    /// Enumerate logins that currently have a token in the store. Used by
    /// Settings → Accounts and onboarding "switch account" affordances.
    /// Returns an empty list when none are present, not a failure.
    abstract ListStoredAccountsAsync: CancellationToken -> Task<OperationResult<IReadOnlyList<string>>>

/// Default <see cref="IGitHubAuth"/> implementation. Composes the three
/// collaborators: HTTP transport (for talking to GitHub), credential store
/// (for the persistence side), and a clock (for deciding device-code
/// expiry). All collaborators are injected so tests can substitute fakes
/// without touching the network or the user's credential vault.
///
/// <para><paramref name="delayAsync"/> abstracts the polling delay so unit
/// tests drive the loop synchronously without wall-clock waits. Production
/// callers should pass <see cref="System.Threading.Tasks.Task.Delay"/> via
/// <see cref="DefaultDelay"/>.</para>
type GitHubAuth
    (
        transport: IHttpTransport,
        credentialStore: ICredentialStore,
        now: Func<DateTimeOffset>,
        delayAsync: Func<TimeSpan, CancellationToken, Task>
    ) =

    let tokenStore = TokenStore credentialStore
    let nowFn () = now.Invoke()
    let delayFn ts ct = delayAsync.Invoke(ts, ct)

    /// Convenience constructor: real <see cref="DateTimeOffset.UtcNow"/>
    /// clock and real <see cref="Task.Delay"/>. Production use.
    new(transport: IHttpTransport, credentialStore: ICredentialStore) =
        GitHubAuth(
            transport,
            credentialStore,
            Func<DateTimeOffset>(fun () -> DateTimeOffset.UtcNow),
            Func<TimeSpan, CancellationToken, Task>(fun ts ct -> Task.Delay(ts, ct))
        )

    interface IGitHubAuth with
        member _.BeginDeviceFlowAsync(config, ct) =
            DeviceFlow.beginAsync transport config nowFn ct

        member _.CompleteDeviceFlowAsync(config, challenge, ct) =
            task {
                let! tokenResult = DeviceFlow.completeAsync transport config challenge nowFn delayFn ct

                match tokenResult with
                | Failure e -> return Failure e
                | Success token ->
                    let! loginResult = DeviceFlow.resolveLoginAsync transport config token.Token ct

                    match loginResult with
                    | Failure e -> return Failure e
                    | Success login ->
                        let tokenWithLogin = { token with Account = login }
                        let! saveResult = tokenStore.SaveAsync(tokenWithLogin, ct)

                        match saveResult with
                        | Failure e -> return Failure e
                        | Success() -> return Success tokenWithLogin
            }

        member _.SignInWithPatAsync(config, pat, ct) =
            task {
                if String.IsNullOrWhiteSpace pat then
                    return Failure(AveliaError.Validation "PAT cannot be empty.")
                else
                    let! loginResult = DeviceFlow.resolveLoginAsync transport config pat ct

                    match loginResult with
                    | Failure e -> return Failure e
                    | Success login ->
                        let token =
                            { Account = login
                              Token = pat
                              Method = AuthMethod.Pat
                              // PAT scopes aren't returned by GET /user.
                              // We leave the array empty; the GitHub.Auth
                              // client can re-read the x-oauth-scopes
                              // response header on its first call if it
                              // ever needs to display them.
                              ScopesGranted = Array.empty
                              ExpiresAt = DateTimeOffset.MaxValue
                              RefreshToken = ""
                              RefreshExpiresAt = DateTimeOffset.MaxValue }

                        let! saveResult = tokenStore.SaveAsync(token, ct)

                        match saveResult with
                        | Failure e -> return Failure e
                        | Success() -> return Success token
            }

        member _.LoadStoredTokenAsync(login, ct) = tokenStore.LoadAsync(login, ct)

        member _.SignOutAsync(login, ct) = tokenStore.DeleteAsync(login, ct)

        member _.ListStoredAccountsAsync(ct: CancellationToken) : Task<OperationResult<IReadOnlyList<string>>> =
            // The cross-platform <see cref="ICredentialStore"/> doesn't
            // expose enumeration (each backend's enumeration story differs
            // wildly). On Windows we fall back to direct Meziantou usage —
            // future macOS/Linux backends will need their own enumeration
            // path or a separate <c>IListableCredentialStore</c> trait.
            Task.Run(
                (fun () ->
                    try
                        let creds = CredentialManager.EnumerateCredentials(CredentialKey.GitHubPrefix + "*")

                        let logins =
                            creds
                            |> Seq.choose (fun c ->
                                match box c with
                                | null -> None
                                | _ ->
                                    match CredentialKey.tryParseGitHubAccount c.ApplicationName with
                                    | ValueSome login -> Some login
                                    | ValueNone -> None)
                            |> Seq.distinct
                            |> Seq.toArray

                        Success(logins :> IReadOnlyList<string>)
                    with
                    | :? System.PlatformNotSupportedException as ex ->
                        Failure(AveliaError.External("credential-manager", ex.Message))
                    | ex -> Failure(AveliaError.External("credential-manager", ex.Message))),
                ct
            )

// ============================================================================
//  Known client ids — public per-environment knobs
//
//  These are GitHub-allocated identifiers, not secrets. A device-flow
//  client id is harmless on its own — the actual scoping comes from the
//  user's approval at <c>github.com/login/device</c>. Keeping them in code
//  rather than configuration is intentional: changing the value requires a
//  shipped binary update so attackers can't steer the install to a hostile
//  device-flow client by editing config on disk.
//
//  The real values land in B-12 when onboarding is wired. For now we
//  expose placeholder constants the test suite uses; the production
//  composition root will substitute real ids at construction.
// ============================================================================

[<RequireQualifiedAccess>]
module KnownClients =

    /// Placeholder for the GitHub App's device-flow client id. Replace with
    /// the real id in <c>Composition.fs</c> at B-12. Held here so the
    /// shape (host + id + scopes) is documented in one place even before
    /// the value lands.
    let gitHubAppPublic: DeviceFlowConfig =
        { Host = "https://github.com"
          ClientId = "" // set in Composition.fs
          // GitHub App permissions are app-defined; no scopes go on the
          // wire. RFC 8628 permits an empty scope param.
          Scopes = Array.empty
          Method = AuthMethod.GitHubApp }

    /// Placeholder for the OAuth App fallback (used when the user's
    /// enterprise disallows GitHub Apps). Scopes mirror the design's
    /// "repo + read:user" requirement.
    let oauthAppPublic: DeviceFlowConfig =
        { Host = "https://github.com"
          ClientId = "" // set in Composition.fs
          Scopes = [| "repo"; "read:user" |]
          Method = AuthMethod.OAuthApp }
