namespace Avelia.Vcs.GitHub.Auth

open System
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

// ============================================================================
//  Device-flow client + PAT resolution — thin wrappers around Octokit
//
//  Octokit owns the protocol (RFC 8628), the HTTP boundary, and the
//  polling loop. This module's job is the boundary mapping:
//    * Our domain types ⇄ Octokit DTOs
//    * Octokit exceptions ⇄ <see cref="AveliaError"/>
//
//  The orchestration that composes these into a public
//  <see cref="IGitHubAuth"/> lives in <c>Auth.fs</c>.
// ============================================================================

[<RequireQualifiedAccess>]
module DeviceFlow =

    /// Translate an Octokit exception (Api / Authorization / etc.) to the
    /// Avelia error vocabulary. The auth flow's failures are user-facing
    /// (renderable as a banner / dialog) so we collapse Octokit's
    /// status-code-keyed hierarchy down to the cases the shell can render.
    let mapException (ex: exn) : AveliaError =
        match ex with
        | :? Octokit.AuthorizationException -> AveliaError.Unauthorized
        | :? Octokit.NotFoundException as nf -> AveliaError.NotFound nf.Message
        | :? Octokit.ApiValidationException as v -> AveliaError.Validation v.Message
        | :? Octokit.ApiException as api -> AveliaError.External("github", $"HTTP {int api.StatusCode}: {api.Message}")
        | :? System.Net.Http.HttpRequestException as net -> AveliaError.Network net.Message
        | _ -> AveliaError.External("github", ex.Message)

    /// Begin the flow — fetch a device code via Octokit. Returns our
    /// <see cref="DeviceCodeChallenge"/> mapped from the upstream
    /// <see cref="Octokit.OauthDeviceFlowResponse"/>. The caller displays
    /// <c>UserCode</c> + <c>VerificationUri</c> to the user and then
    /// awaits <see cref="completeAsync"/>.
    let beginAsync
        (oauthClient: Octokit.IOauthClient)
        (cfg: DeviceFlowConfig)
        (now: unit -> DateTimeOffset)
        (ct: CancellationToken)
        : Task<OperationResult<DeviceCodeChallenge>> =
        task {
            try
                let req = Octokit.OauthDeviceFlowRequest cfg.ClientId

                for scope in cfg.Scopes do
                    req.Scopes.Add scope

                let! resp = oauthClient.InitiateDeviceFlow req
                // Octokit's Interval is in seconds; clamp non-positive to
                // 5s (the RFC 8628 default) so a buggy server can't push
                // us into a hot poll loop.
                let interval =
                    if resp.Interval <= 0 then
                        TimeSpan.FromSeconds 5.0
                    else
                        TimeSpan.FromSeconds(float resp.Interval)

                let expires =
                    if resp.ExpiresIn <= 0 then
                        (now ()).AddMinutes 15.0
                    else
                        (now ()).AddSeconds(float resp.ExpiresIn)

                return
                    Success
                        { UserCode = if isNull resp.UserCode then "" else resp.UserCode
                          VerificationUri =
                            if isNull resp.VerificationUri then
                                ""
                            else
                                resp.VerificationUri
                          DeviceCode = if isNull resp.DeviceCode then "" else resp.DeviceCode
                          Interval = interval
                          ExpiresAt = expires }
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException ct)
            | ex -> return Failure(mapException ex)
        }

    /// Build the Octokit-shaped response that the polling helper expects.
    /// We carry <c>DeviceCode</c>, <c>Interval</c>, and recompute
    /// <c>ExpiresIn</c> from our wall-clock-relative <c>ExpiresAt</c>.
    /// <c>UserCode</c> and <c>VerificationUri</c> aren't used by the
    /// polling endpoint but are required by the constructor.
    ///
    /// <para>No interval clamping here — the upstream <see cref="beginAsync"/>
    /// already replaced a server-returned 0 with the RFC 8628 default
    /// of 5s. Passing whatever the caller's <c>DeviceCodeChallenge</c>
    /// carries lets tests drive the polling loop with <c>TimeSpan.Zero</c>
    /// for synchronous behaviour.</para>
    let private toOctokitResponse
        (challenge: DeviceCodeChallenge)
        (now: DateTimeOffset)
        : Octokit.OauthDeviceFlowResponse =
        let expiresIn =
            let delta = challenge.ExpiresAt - now
            if delta <= TimeSpan.Zero then 0 else int delta.TotalSeconds

        let intervalSecs = max 0 (int challenge.Interval.TotalSeconds)

        Octokit.OauthDeviceFlowResponse(
            challenge.DeviceCode,
            challenge.UserCode,
            challenge.VerificationUri,
            expiresIn,
            intervalSecs
        )

    /// Map an Octokit <see cref="Octokit.OauthToken"/> to our
    /// <see cref="GitHubAccessToken"/>. <c>Account</c> stays empty —
    /// the caller resolves the login via <see cref="resolveLoginAsync"/>
    /// before persisting.
    let private fromOctokitToken
        (cfg: DeviceFlowConfig)
        (token: Octokit.OauthToken)
        (now: DateTimeOffset)
        : GitHubAccessToken =
        let scopes =
            match box token.Scope with
            | null -> Array.empty
            | _ -> token.Scope |> Seq.toArray

        let expires =
            if token.ExpiresIn <= 0 then
                DateTimeOffset.MaxValue
            else
                now.AddSeconds(float token.ExpiresIn)

        let refresh =
            match box token.RefreshToken with
            | null -> ""
            | _ -> token.RefreshToken

        let refreshExpires =
            if token.RefreshTokenExpiresIn <= 0 then
                DateTimeOffset.MaxValue
            else
                now.AddSeconds(float token.RefreshTokenExpiresIn)

        { Account = ""
          Token = if isNull token.AccessToken then "" else token.AccessToken
          Method = cfg.Method
          ScopesGranted = scopes
          ExpiresAt = expires
          RefreshToken = refresh
          RefreshExpiresAt = refreshExpires }

    /// Poll the token endpoint via Octokit until the user approves or the
    /// code expires. Octokit's loop honours <c>authorization_pending</c>
    /// (continue) and <c>slow_down</c> (bump interval by 5s); terminal
    /// signals (<c>expired_token</c>, <c>access_denied</c>) surface as
    /// <see cref="Octokit.ApiException"/> which we map back to
    /// <see cref="AveliaError"/>.
    ///
    /// <para>The <c>Task.Delay</c> inside Octokit's loop is keyed on
    /// <c>challenge.Interval</c>; tests pass <c>Interval = TimeSpan.Zero</c>
    /// so polling runs synchronously.</para>
    let completeAsync
        (oauthClient: Octokit.IOauthClient)
        (cfg: DeviceFlowConfig)
        (challenge: DeviceCodeChallenge)
        (now: unit -> DateTimeOffset)
        (ct: CancellationToken)
        : Task<OperationResult<GitHubAccessToken>> =
        task {
            try
                let octokitResp = toOctokitResponse challenge (now ())
                let! token = oauthClient.CreateAccessTokenForDeviceFlow(cfg.ClientId, octokitResp)
                return Success(fromOctokitToken cfg token (now ()))
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException ct)
            | ex -> return Failure(mapException ex)
        }

    /// Resolve the GitHub login for an access token via
    /// <c>GET /user</c>. Used to populate the <c>Account</c> field of a
    /// freshly-acquired token before persisting it.
    let resolveLoginAsync
        (githubClient: Octokit.IGitHubClient)
        (ct: CancellationToken)
        : Task<OperationResult<string>> =
        task {
            try
                let! user = githubClient.User.Current()

                if isNull user || String.IsNullOrEmpty user.Login then
                    return Failure(AveliaError.External("github", "GET /user returned no login."))
                else
                    return Success user.Login
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException ct)
            | ex -> return Failure(mapException ex)
        }
