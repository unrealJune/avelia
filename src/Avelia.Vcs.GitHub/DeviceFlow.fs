namespace Avelia.Vcs.GitHub.Auth

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

// ============================================================================
//  Device-flow client + PAT validation
//
//  Implements RFC 8628 (OAuth Device Authorization Grant) against GitHub's
//  endpoints:
//    * <c>POST /login/device/code</c>     — fetch a challenge.
//    * <c>POST /login/oauth/access_token</c> — poll for the token.
//
//  Plus PAT validation via <c>GET /user</c>: if the PAT is good we get a
//  200 with the login back; 401 = bad PAT.
//
//  All HTTP goes through <see cref="IHttpTransport"/> so tests stub the
//  network without going to GitHub.
// ============================================================================

// ---------------------------------------------------------------------------
//  Default HTTP transport — production wrapper over System.Net.Http
// ---------------------------------------------------------------------------

/// Production implementation of <see cref="IHttpTransport"/> backed by
/// <see cref="HttpClient"/>. Single shared client per instance — callers
/// (factory in <c>Auth.fs</c>) own one per process and dispose on shutdown
/// so the socket pool isn't churned per request.
type HttpTransport(httpClient: HttpClient) =

    /// The product name registered in the <c>User-Agent</c> header. GitHub
    /// rejects requests without a UA; this label also shows up in their
    /// rate-limit dashboards so we can debug noisy clients.
    [<Literal>]
    static let ProductName = "Avelia"

    [<Literal>]
    static let ProductVersion = "0.1"

    /// Convenience constructor for the common case — the caller doesn't
    /// already have an <see cref="HttpClient"/>. Creates one with a 100s
    /// default timeout (matches HttpClient default) and a single shared
    /// socket handler.
    new() = new HttpTransport(new HttpClient())

    interface IHttpTransport with
        member _.SendAsync
            (method: string, url: string, bearerToken: string, formBody: seq<string * string>, ct: CancellationToken)
            =
            task {
                try
                    use req = new HttpRequestMessage(HttpMethod(method.ToUpperInvariant()), url)
                    // GitHub returns form-urlencoded by default for
                    // OAuth endpoints unless we ask for JSON.
                    req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
                    req.Headers.UserAgent.Add(ProductInfoHeaderValue(ProductName, ProductVersion))

                    if not (String.IsNullOrEmpty bearerToken) then
                        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearerToken)

                    // Materialize once so a generator-style seq can't be
                    // walked twice (we both check emptiness and iterate).
                    let pairs = formBody |> Seq.toList

                    if not pairs.IsEmpty then
                        let kv =
                            pairs
                            |> List.map (fun (k, v) -> KeyValuePair<string, string>(k, v))
                            |> List.toArray

                        req.Content <- new FormUrlEncodedContent(kv)

                    let! resp = httpClient.SendAsync(req, ct)
                    let! body = resp.Content.ReadAsStringAsync ct

                    let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

                    for h in resp.Headers do
                        headers.[h.Key] <- String.Join(",", h.Value)

                    match box resp.Content with
                    | null -> ()
                    | _ ->
                        for h in resp.Content.Headers do
                            headers.[h.Key] <- String.Join(",", h.Value)

                    return
                        Success
                            { StatusCode = int resp.StatusCode
                              Body = body
                              Headers = headers :> IReadOnlyDictionary<_, _> }
                with
                | :? OperationCanceledException ->
                    // Re-throw cancellation so the caller's task is
                    // observed as cancelled (consistent with the rest of
                    // the codebase, which uses cancellation tokens for
                    // user-initiated aborts).
                    return raise (OperationCanceledException(ct))
                | :? HttpRequestException as ex -> return Failure(AveliaError.Network ex.Message)
                | ex -> return Failure(AveliaError.External("http", ex.Message))
            }

    interface IDisposable with
        member _.Dispose() = httpClient.Dispose()

// ---------------------------------------------------------------------------
//  JSON DTOs for the GitHub wire format
// ---------------------------------------------------------------------------

/// Successful response from <c>POST /login/device/code</c>. Public so the
/// <c>DeviceFlow</c> module's testable helpers (which take the DTO directly
/// for state-machine unit tests) don't leak less-accessible types.
///
/// Field types are nullable because JSON deserialization can produce <c>null</c>
/// for properties the wire payload omits.
type DeviceCodeResponse() =
    member val device_code: string | null = "" with get, set
    member val user_code: string | null = "" with get, set
    member val verification_uri: string | null = "" with get, set
    member val verification_uri_complete: string | null = "" with get, set
    member val expires_in: int = 0 with get, set
    member val interval: int = 5 with get, set

/// Successful response from <c>POST /login/oauth/access_token</c>. Errors
/// surface as <c>error</c> + <c>error_description</c> per RFC 8628 §3.5.
type AccessTokenResponse() =
    member val access_token: string | null = "" with get, set
    member val token_type: string | null = "" with get, set
    member val scope: string | null = "" with get, set
    member val expires_in: int = 0 with get, set
    member val refresh_token: string | null = "" with get, set
    member val refresh_token_expires_in: int = 0 with get, set
    member val error: string | null = "" with get, set
    member val error_description: string | null = "" with get, set

/// Successful response from <c>GET /user</c>.
type UserResponse() =
    member val login: string | null = "" with get, set

// ---------------------------------------------------------------------------
//  Device-flow client
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module DeviceFlow =

    let private jsonOptions =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o

    let private deserialize<'T when 'T: not struct and 'T: not null> (body: string) : Result<'T, AveliaError> =
        try
            let v: 'T | null = JsonSerializer.Deserialize<'T>(body, jsonOptions)

            match v with
            | null -> Error(AveliaError.External("github", "Empty JSON response."))
            | actual -> Ok actual
        with
        | :? JsonException as ex -> Error(AveliaError.External("github", $"Malformed JSON: {ex.Message}"))
        | ex -> Error(AveliaError.External("github", ex.Message))

    /// Helper: collapse a possibly-null string to its empty-sentinel.
    let inline private orEmpty (s: string | null) : string = if isNull s then "" else nonNull s

    /// Build the URL for a device-flow endpoint against
    /// <c>cfg.Host</c>. <c>Host</c> is normalised — trailing slash
    /// stripped — so <c>"https://github.com"</c> and
    /// <c>"https://github.com/"</c> behave the same.
    let private endpointUrl (cfg: DeviceFlowConfig) (path: string) : string =
        let host =
            if String.IsNullOrWhiteSpace cfg.Host then
                "https://github.com"
            else
                cfg.Host.TrimEnd '/'

        host + path

    /// Build the URL for an API endpoint. GitHub.com routes API calls to
    /// <c>api.github.com</c>; GHES installs route everything under
    /// <c>{host}/api/v3</c>.
    let private apiUrl (cfg: DeviceFlowConfig) (path: string) : string =
        let host =
            if String.IsNullOrWhiteSpace cfg.Host then
                "https://github.com"
            else
                cfg.Host.TrimEnd '/'

        // Public github.com → api.github.com; GHES → {host}/api/v3.
        if
            host.Equals("https://github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("http://github.com", StringComparison.OrdinalIgnoreCase)
        then
            "https://api.github.com" + path
        else
            host + "/api/v3" + path

    /// Begin the flow — fetch a device code from GitHub. The returned
    /// challenge carries everything the UI needs to display (<c>UserCode</c>,
    /// <c>VerificationUri</c>) plus the opaque <c>DeviceCode</c> for the
    /// polling step.
    let beginAsync
        (transport: IHttpTransport)
        (cfg: DeviceFlowConfig)
        (now: unit -> DateTimeOffset)
        (ct: CancellationToken)
        : Task<OperationResult<DeviceCodeChallenge>> =
        task {
            let scopesParam = String.Join(",", cfg.Scopes)

            let body: (string * string) list =
                if String.IsNullOrEmpty scopesParam then
                    [ "client_id", cfg.ClientId ]
                else
                    [ "client_id", cfg.ClientId; "scope", scopesParam ]

            let url = endpointUrl cfg "/login/device/code"
            let! resp = transport.SendAsync("POST", url, "", body, ct)

            match resp with
            | Failure e -> return Failure e
            | Success r when r.StatusCode < 200 || r.StatusCode >= 300 ->
                return Failure(AveliaError.External("github", $"HTTP {r.StatusCode}: {r.Body}"))
            | Success r ->
                match deserialize<DeviceCodeResponse> r.Body with
                | Error e -> return Failure e
                | Ok dto ->
                    let deviceCode = orEmpty dto.device_code

                    if String.IsNullOrEmpty deviceCode then
                        return Failure(AveliaError.External("github", "Response missing device_code."))
                    else
                        let interval =
                            if dto.interval <= 0 then
                                TimeSpan.FromSeconds 5.0
                            else
                                TimeSpan.FromSeconds(float dto.interval)

                        let expires =
                            if dto.expires_in <= 0 then
                                (now ()).AddMinutes 15.0
                            else
                                (now ()).AddSeconds(float dto.expires_in)

                        return
                            Success
                                { UserCode = orEmpty dto.user_code
                                  VerificationUri = orEmpty dto.verification_uri
                                  VerificationUriComplete = orEmpty dto.verification_uri_complete
                                  DeviceCode = deviceCode
                                  Interval = interval
                                  ExpiresAt = expires }
        }

    /// One poll iteration — issued by <see cref="completeAsync"/>. Surfaced
    /// for testability so a unit test can drive the polling state machine
    /// step-by-step instead of relying on wall-clock delays.
    let pollOnceAsync
        (transport: IHttpTransport)
        (cfg: DeviceFlowConfig)
        (deviceCode: string)
        (ct: CancellationToken)
        : Task<OperationResult<AccessTokenResponse>> =
        task {
            let body =
                [ "client_id", cfg.ClientId
                  "device_code", deviceCode
                  "grant_type", "urn:ietf:params:oauth:grant-type:device_code" ]

            let url = endpointUrl cfg "/login/oauth/access_token"
            let! resp = transport.SendAsync("POST", url, "", body, ct)

            match resp with
            | Failure e -> return Failure e
            | Success r ->
                // GitHub returns 200 even on errors like authorization_pending
                // — the discriminator lives in the JSON body's `error` field.
                match deserialize<AccessTokenResponse> r.Body with
                | Error e -> return Failure e
                | Ok dto -> return Success dto
        }

    /// Outcome of one polling step that's still "in progress" (the user
    /// hasn't decided yet). Distinct from the terminal errors
    /// (<c>expired_token</c>, <c>access_denied</c>) so the loop can stay in
    /// the polling state without retrying terminal failures.
    [<RequireQualifiedAccess>]
    type PollStep =
        /// Wait <see cref="Delay"/> and try again.
        | KeepPolling of delay: TimeSpan
        /// Server told us to slow down; the new minimum interval is encoded.
        | SlowDown of newInterval: TimeSpan
        /// Token landed.
        | Acquired of token: GitHubAccessToken
        /// Terminal failure (expired, denied, etc.). Caller surfaces as
        /// <see cref="OperationResult"/> failure.
        | Failed of error: AveliaError

    let private classifyPoll
        (cfg: DeviceFlowConfig)
        (now: unit -> DateTimeOffset)
        (currentInterval: TimeSpan)
        (dto: AccessTokenResponse)
        : PollStep =
        let err = orEmpty dto.error

        match err with
        | "" ->
            let accessToken = orEmpty dto.access_token

            if String.IsNullOrEmpty accessToken then
                PollStep.Failed(
                    AveliaError.External("github", "Successful response missing access_token (no error returned).")
                )
            else
                let scopeRaw = orEmpty dto.scope

                let scopes =
                    if String.IsNullOrEmpty scopeRaw then
                        Array.empty
                    else
                        scopeRaw.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)

                let expires =
                    if dto.expires_in <= 0 then
                        DateTimeOffset.MaxValue
                    else
                        (now ()).AddSeconds(float dto.expires_in)

                let refreshExpires =
                    if dto.refresh_token_expires_in <= 0 then
                        DateTimeOffset.MaxValue
                    else
                        (now ()).AddSeconds(float dto.refresh_token_expires_in)

                let refresh = orEmpty dto.refresh_token

                PollStep.Acquired
                    { Account = "" // resolved later via GET /user
                      Token = accessToken
                      Method = cfg.Method
                      ScopesGranted = scopes
                      ExpiresAt = expires
                      RefreshToken = refresh
                      RefreshExpiresAt = refreshExpires }
        | "authorization_pending" -> PollStep.KeepPolling currentInterval
        | "slow_down" ->
            // RFC 8628 §3.5: server-mandated bump of 5+ seconds.
            PollStep.SlowDown(currentInterval + TimeSpan.FromSeconds 5.0)
        | "expired_token" -> PollStep.Failed(AveliaError.External("github", "Device code expired."))
        | "access_denied" -> PollStep.Failed AveliaError.Unauthorized
        | other ->
            let detailDesc = orEmpty dto.error_description

            let detail =
                if String.IsNullOrEmpty detailDesc then
                    other
                else
                    $"{other}: {detailDesc}"

            PollStep.Failed(AveliaError.External("github", detail))

    /// Public for tests: classify one polling DTO. Pure function; given the
    /// same inputs always returns the same step.
    let classifyPollForTesting cfg now currentInterval dto =
        classifyPoll cfg now currentInterval dto

    /// Poll the token endpoint until the user approves or the code expires.
    /// <paramref name="delayAsync"/> abstracts <see cref="Task.Delay"/> so
    /// unit tests can substitute a synchronous tick.
    let completeAsync
        (transport: IHttpTransport)
        (cfg: DeviceFlowConfig)
        (challenge: DeviceCodeChallenge)
        (now: unit -> DateTimeOffset)
        (delayAsync: TimeSpan -> CancellationToken -> Task)
        (ct: CancellationToken)
        : Task<OperationResult<GitHubAccessToken>> =
        task {
            let mutable interval = challenge.Interval
            let mutable outcome: OperationResult<GitHubAccessToken> voption = ValueNone

            while outcome.IsNone && not ct.IsCancellationRequested do
                if (now ()) > challenge.ExpiresAt then
                    outcome <- ValueSome(Failure(AveliaError.External("github", "Device code expired locally.")))
                else
                    do! delayAsync interval ct
                    let! pollResult = pollOnceAsync transport cfg challenge.DeviceCode ct

                    match pollResult with
                    | Failure e -> outcome <- ValueSome(Failure e)
                    | Success dto ->
                        match classifyPoll cfg now interval dto with
                        | PollStep.KeepPolling _ -> ()
                        | PollStep.SlowDown newInterval -> interval <- newInterval
                        | PollStep.Acquired token -> outcome <- ValueSome(Success token)
                        | PollStep.Failed err -> outcome <- ValueSome(Failure err)

            ct.ThrowIfCancellationRequested()

            return
                match outcome with
                | ValueSome o -> o
                | ValueNone ->
                    // Unreachable in practice — the loop only exits when
                    // outcome is set or the token cancels (which throws
                    // above). Treat as defensive.
                    Failure(AveliaError.Internal "Polling loop exited without a verdict.")
        }

    // -------------------------------------------------------------------
    //  PAT validation
    // -------------------------------------------------------------------

    /// Resolve the GitHub login for an access token (any method — PAT,
    /// device-flow, etc.). Surfaces the same outcome the auth flow needs:
    /// <c>Failure Unauthorized</c> on 401, <c>Failure (External ...)</c> on
    /// transport/server errors, <c>Success login</c> on 200.
    let resolveLoginAsync
        (transport: IHttpTransport)
        (cfg: DeviceFlowConfig)
        (token: string)
        (ct: CancellationToken)
        : Task<OperationResult<string>> =
        task {
            let url = apiUrl cfg "/user"
            let! resp = transport.SendAsync("GET", url, token, Seq.empty, ct)

            match resp with
            | Failure e -> return Failure e
            | Success r ->
                match r.StatusCode with
                | 200 ->
                    match deserialize<UserResponse> r.Body with
                    | Error e -> return Failure e
                    | Ok user ->
                        let login = orEmpty user.login

                        if String.IsNullOrEmpty login then
                            return Failure(AveliaError.External("github", "GET /user returned no login."))
                        else
                            return Success login
                | 401 -> return Failure AveliaError.Unauthorized
                | code -> return Failure(AveliaError.External("github", $"HTTP {code}: {r.Body}"))
        }
