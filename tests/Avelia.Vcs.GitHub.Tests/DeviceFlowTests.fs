module Avelia.Vcs.GitHub.Tests.DeviceFlowTests

open System
open System.Net
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Auth
open Avelia.Vcs.GitHub.Tests.OctokitHttpStub

// ----------------------------------------------------------------------------
//  Drive DeviceFlow against an Octokit client wired to a scripted
//  IHttpClient. The tests exercise the actual Octokit pipeline (request
//  building, serializer, OauthClient state machine) — much higher
//  fidelity than the previous hand-rolled IHttpTransport stub.
//
//  Polling Interval = 0 keeps Octokit's internal Task.Delay loop
//  synchronous so tests don't pay wall-clock cost.
// ----------------------------------------------------------------------------

let private cfg: DeviceFlowConfig =
    { Host = "https://github.com"
      ClientId = "Iv1.testclient"
      Scopes = [| "repo"; "read:user" |]
      Method = AuthMethod.OAuthApp }

let private nowFixed () =
    DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero)

let private ct = CancellationToken.None

/// Build an Octokit GitHubClient pointed at the scripted IHttpClient.
/// Anonymous credentials — device-flow handshake doesn't need a bearer.
let private buildClient (http: ScriptedHttpClient) : Octokit.GitHubClient =
    let creds = Octokit.Credentials.Anonymous

    let credStore =
        { new Octokit.ICredentialStore with
            member _.GetCredentials() = Task.FromResult creds }

    let conn =
        Octokit.Connection(
            Octokit.ProductHeaderValue("Avelia", "0.1"),
            Octokit.GitHubClient.GitHubApiUrl,
            credStore,
            http,
            Octokit.Internal.SimpleJsonSerializer()
        )

    Octokit.GitHubClient conn

// ============================================================================
//  beginAsync
// ============================================================================

[<Fact>]
let ``beginAsync hits /login/device/code with the configured client id and scopes`` () =
    // GitHub's response is x-www-form-urlencoded by default but
    // OauthClient asks for JSON via the Accept header.
    let body =
        """{"device_code":"abc123","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""

    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result =
        DeviceFlow.beginAsync client.Oauth cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Success c ->
        Assert.Equal("WDJB-MJHT", c.UserCode)
        Assert.Equal("abc123", c.DeviceCode)
        Assert.Equal("https://github.com/login/device", c.VerificationUri)
        Assert.Equal(TimeSpan.FromSeconds 5.0, c.Interval)
        Assert.Equal(nowFixed().AddSeconds 900.0, c.ExpiresAt)
    | Failure e -> Assert.Fail $"Expected success: {e}"

    Assert.Single(http.Recorded) |> ignore
    let req = http.Recorded.[0]
    // Octokit's OauthClient rewrites api.github.com → github.com for
    // OAuth endpoints (per its constructor logic).
    Assert.Equal("https://github.com/login/device/code", req.Url)
    Assert.Equal(System.Net.Http.HttpMethod.Post, req.Method)

    // Body is FormUrlEncodedContent; verify the parameters Octokit posted.
    let formBody = (readFormBodyAsync req.Body).GetAwaiter().GetResult()
    Assert.Contains("client_id=Iv1.testclient", formBody)
    // Octokit serializes scopes as comma-separated "scope" param.
    Assert.Contains("scope=repo%2Cread%3Auser", formBody)

[<Fact>]
let ``beginAsync surfaces server 4xx as External`` () =
    let body = """{"message":"Bad request"}"""
    let http = new ScriptedHttpClient([ okJson HttpStatusCode.BadRequest body ])
    let client = buildClient http

    let result =
        DeviceFlow.beginAsync client.Oauth cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", _)) -> ()
    | other -> Assert.Fail $"Expected External (github), got {other}"

[<Fact>]
let ``beginAsync clamps non-positive interval to default 5s`` () =
    let body =
        """{"device_code":"d","user_code":"U","verification_uri":"v","expires_in":60,"interval":0}"""

    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result =
        DeviceFlow.beginAsync client.Oauth cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Success c -> Assert.Equal(TimeSpan.FromSeconds 5.0, c.Interval)
    | Failure e -> Assert.Fail $"Expected success: {e}"

// ============================================================================
//  completeAsync — Octokit's polling loop driven via scripted responses
// ============================================================================

let private challenge: DeviceCodeChallenge =
    { UserCode = "WDJB-MJHT"
      VerificationUri = "https://github.com/login/device"
      DeviceCode = "device-abc"
      // Zero interval ⇒ Octokit's Task.Delay returns immediately;
      // polling runs synchronously without wall-clock waits.
      Interval = TimeSpan.Zero
      ExpiresAt = nowFixed().AddMinutes 15.0 }

[<Fact>]
let ``completeAsync polls through pending then returns the acquired token`` () =
    let pendingBody = """{"error":"authorization_pending"}"""

    let successBody =
        """{"access_token":"ghu_token","token_type":"bearer","scope":"repo,read:user","expires_in":28800}"""

    let http = new ScriptedHttpClient([ ok pendingBody; ok successBody ])
    let client = buildClient http

    let result =
        DeviceFlow.completeAsync client.Oauth cfg challenge nowFixed ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Success(t: PendingGitHubToken) ->
        Assert.Equal("ghu_token", t.Token)
        Assert.Equal<string[]>([| "repo"; "read:user" |], t.ScopesGranted)
        Assert.Equal(AuthMethod.OAuthApp, t.Method)
        Assert.Equal(nowFixed().AddSeconds 28800.0, t.ExpiresAt)
    // PendingGitHubToken has no Account field — login resolution
    // happens at the orchestration layer in Auth.fs, not here.
    | Failure e -> Assert.Fail $"Expected success: {e}"

    // Two POSTs to the OAuth token endpoint with the right device code.
    Assert.Equal(2, http.Recorded.Count)

    for req in http.Recorded do
        Assert.Equal("https://github.com/login/oauth/access_token", req.Url)
        Assert.Equal(System.Net.Http.HttpMethod.Post, req.Method)
        let body = (readFormBodyAsync req.Body).GetAwaiter().GetResult()
        Assert.Contains("device_code=device-abc", body)
        Assert.Contains("client_id=Iv1.testclient", body)
        Assert.Contains("grant_type=urn", body)

[<Fact>]
let ``completeAsync slow_down then success`` () =
    let slowBody = """{"error":"slow_down"}"""

    let successBody =
        """{"access_token":"tok","token_type":"bearer","scope":"repo","expires_in":60}"""

    let http = new ScriptedHttpClient([ ok slowBody; ok successBody ])
    let client = buildClient http

    let result =
        DeviceFlow.completeAsync client.Oauth cfg challenge nowFixed ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Success t -> Assert.Equal("tok", t.Token)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``completeAsync access_denied surfaces as External (terminal)`` () =
    let body =
        """{"error":"access_denied","error_description":"the user said no","error_uri":"https://..."}"""

    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result =
        DeviceFlow.completeAsync client.Oauth cfg challenge nowFixed ct
        |> _.GetAwaiter().GetResult()

    // Octokit throws ApiException with the raw error string for
    // anything other than authorization_pending / slow_down. We map
    // that to External.
    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("access_denied", msg)
    | other -> Assert.Fail $"Expected External (github), got {other}"

[<Fact>]
let ``completeAsync expired_token surfaces as External (terminal)`` () =
    let body = """{"error":"expired_token","error_description":"too late"}"""
    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result =
        DeviceFlow.completeAsync client.Oauth cfg challenge nowFixed ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("expired_token", msg)
    | other -> Assert.Fail $"Expected External (github), got {other}"

// ============================================================================
//  resolveLoginAsync
// ============================================================================

[<Fact>]
let ``resolveLoginAsync returns the login on 200`` () =
    let body = """{"login":"octocat","id":1,"name":"The Octocat"}"""
    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result = DeviceFlow.resolveLoginAsync client ct |> _.GetAwaiter().GetResult()

    match result with
    | Success login -> Assert.Equal("octocat", login.Value)
    | other -> Assert.Fail $"Expected Success 'octocat', got {other}"

    let req = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method)
    Assert.Equal("https://api.github.com/user", req.Url)

[<Fact>]
let ``resolveLoginAsync 401 returns Unauthorized`` () =
    let http =
        new ScriptedHttpClient([ okJson HttpStatusCode.Unauthorized """{"message":"Bad credentials"}""" ])

    let client = buildClient http
    let result = DeviceFlow.resolveLoginAsync client ct |> _.GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"
