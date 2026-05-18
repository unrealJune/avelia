namespace Avelia.Vcs.GitHub.Auth

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub
open Meziantou.Framework.Win32
open Octokit.Internal

// ============================================================================
//  IGitHubAuth — orchestration layer over Octokit + credential store
//
//  Shell consumes this; never sees Octokit. The boundary is small (six
//  methods) and stable across whatever auth backend we end up with.
// ============================================================================

/// Public surface for GitHub authentication. Composed by the shell-side
/// onboarding flow (B-12) and consumed by the rest of the GitHub VCS layer
/// to obtain a token at request time.
///
/// <para>Lifecycle: one <see cref="IGitHubAuth"/> per process, holding the
/// shared Octokit <see cref="IHttpClient"/> factory and credential store.</para>
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

    /// Enumerate logins that currently have a token in the store.
    /// Returns an empty list when none are present, not a failure.
    abstract ListStoredAccountsAsync: CancellationToken -> Task<OperationResult<IReadOnlyList<string>>>

/// Default <see cref="IGitHubAuth"/> implementation. The constructor
/// takes a factory for Octokit's <see cref="IHttpClient"/> so tests can
/// substitute a scripted implementation, and a clock so device-flow
/// expiry timing is deterministic in tests.
///
/// <para>Production callers use the parameterless overload which wires the
/// real <see cref="HttpClientAdapter"/> and the system clock.</para>
type GitHubAuth(httpClientFactory: Func<IHttpClient>, credentialStore: ICredentialStore, now: Func<DateTimeOffset>) =

    let tokenStore = TokenStore credentialStore
    let nowFn () = now.Invoke()

    /// Build an Octokit GitHubClient pointed at <paramref name="cfg.Host"/>
    /// (mapping <c>github.com</c> → <c>api.github.com</c>) carrying the
    /// supplied bearer token (or anonymous when empty).
    let buildOctokitClient (cfg: DeviceFlowConfig) (bearer: string) : Octokit.GitHubClient =
        let baseAddress =
            if
                String.IsNullOrWhiteSpace cfg.Host
                || cfg.Host.Equals("https://github.com", StringComparison.OrdinalIgnoreCase)
            then
                OctokitFactory.defaultApiBaseAddress
            else
                Uri(cfg.Host.TrimEnd '/' + "/api/v3/")

        let credentials = StaticOctokitCredentials bearer :> Octokit.ICredentialStore
        OctokitFactory.buildClient baseAddress credentials (httpClientFactory.Invoke())

    /// Production convenience: real <see cref="HttpClientAdapter"/>
    /// factory + system clock.
    new(credentialStore: ICredentialStore) =
        GitHubAuth(
            OctokitFactory.defaultHttpClientFactory,
            credentialStore,
            Func<DateTimeOffset>(fun () -> DateTimeOffset.UtcNow)
        )

    interface IGitHubAuth with
        member _.BeginDeviceFlowAsync(config, ct) =
            let client = buildOctokitClient config ""
            DeviceFlow.beginAsync client.Oauth config nowFn ct

        member _.CompleteDeviceFlowAsync(config, challenge, ct) =
            task {
                let client = buildOctokitClient config ""
                let! tokenResult = DeviceFlow.completeAsync client.Oauth config challenge nowFn ct

                match tokenResult with
                | Failure e -> return Failure e
                | Success token ->
                    // Now that we have a bearer, build a second client
                    // with the credential to call /user. Octokit's
                    // credential store is read-once-per-request so we
                    // can't mutate the existing client to add the token.
                    let authedClient = buildOctokitClient config token.Token
                    let! loginResult = DeviceFlow.resolveLoginAsync authedClient ct

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
                    let client = buildOctokitClient config pat
                    let! loginResult = DeviceFlow.resolveLoginAsync client ct

                    match loginResult with
                    | Failure e -> return Failure e
                    | Success login ->
                        let token =
                            { Account = login
                              Token = pat
                              Method = AuthMethod.Pat
                              // PAT scopes are returned in the
                              // X-OAuth-Scopes response header; we don't
                              // surface them at this layer. The
                              // ApiClient can read them from
                              // GetLastApiInfo().OauthScopes when
                              // displaying account details.
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
            // <see cref="ICredentialStore"/> doesn't expose enumeration —
            // each backend's enumeration story differs. On Windows we go
            // direct to Meziantou; macOS / Linux backends will need their
            // own listing path or an <c>IListableCredentialStore</c>
            // trait.
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
//  GitHub-allocated identifiers, not secrets. A device-flow client id is
//  harmless on its own — the actual scoping comes from the user's
//  approval at <c>github.com/login/device</c>. Keeping them in code
//  rather than configuration is intentional: changing the value requires
//  a shipped binary update so attackers can't steer the install to a
//  hostile device-flow client by editing config on disk.
// ============================================================================

[<RequireQualifiedAccess>]
module KnownClients =

    /// Placeholder for the GitHub App's device-flow client id. Replace
    /// with the real id in <c>Composition.fs</c> at B-12.
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
