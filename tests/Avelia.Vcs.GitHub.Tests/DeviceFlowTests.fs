module Avelia.Vcs.GitHub.Tests.DeviceFlowTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub.Auth

// ----------------------------------------------------------------------------
//  Tests drive DeviceFlow against a stubbed IHttpTransport. The stub records
//  every request the device-flow code makes so we can assert on URLs and
//  bodies, and returns scripted responses so we can exercise:
//   * success paths (begin + poll-to-token),
//   * RFC 8628 in-progress signals (authorization_pending, slow_down),
//   * terminal errors (expired_token, access_denied),
//   * transport failures (network down),
//   * 4xx / 5xx from the server.
// ----------------------------------------------------------------------------

let private emptyHeaders: IReadOnlyDictionary<string, string> =
    Dictionary<string, string>() :> IReadOnlyDictionary<_, _>

type private RecordedRequest =
    { Method: string
      Url: string
      Bearer: string
      Body: (string * string) list }

/// Stub transport whose response sequence is queued by the test. Records
/// every call for post-hoc assertion.
type private QueuedTransport(responses: OperationResult<HttpResponse> seq) =
    let queue = Queue(responses)
    let recorded = ResizeArray<RecordedRequest>()

    member _.Recorded = recorded :> IReadOnlyList<_>

    interface IHttpTransport with
        member _.SendAsync
            (method: string, url: string, bearerToken: string, formBody: seq<string * string>, _ct: CancellationToken)
            =
            recorded.Add
                { Method = method
                  Url = url
                  Bearer = bearerToken
                  Body = formBody |> Seq.toList }

            if queue.Count = 0 then
                Task.FromResult(Failure(AveliaError.Internal "No more scripted responses."))
            else
                Task.FromResult(queue.Dequeue())

let private ok (status: int) (body: string) : OperationResult<HttpResponse> =
    Success
        { StatusCode = status
          Body = body
          Headers = emptyHeaders }

let private cfg: DeviceFlowConfig =
    { Host = "https://github.com"
      ClientId = "Iv1.testclient"
      Scopes = [| "repo"; "read:user" |]
      Method = AuthMethod.OAuthApp }

let private nowFixed () =
    DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero)

let private noDelay (_: TimeSpan) (_: CancellationToken) : Task = Task.CompletedTask

let private ct = CancellationToken.None

// ----------------------------------------------------------------------------
//  beginAsync
// ----------------------------------------------------------------------------

[<Fact>]
let ``beginAsync parses a successful device-code response`` () =
    let body =
        """{
          "device_code": "abc123",
          "user_code": "WDJB-MJHT",
          "verification_uri": "https://github.com/login/device",
          "verification_uri_complete": "https://github.com/login/device?user_code=WDJB-MJHT",
          "expires_in": 900,
          "interval": 5
        }"""

    let transport = QueuedTransport [ ok 200 body ]

    let result =
        DeviceFlow.beginAsync transport cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Success c ->
        Assert.Equal("WDJB-MJHT", c.UserCode)
        Assert.Equal("abc123", c.DeviceCode)
        Assert.Equal("https://github.com/login/device", c.VerificationUri)
        Assert.Equal(TimeSpan.FromSeconds 5.0, c.Interval)
        Assert.Equal(nowFixed().AddSeconds 900.0, c.ExpiresAt)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``beginAsync targets the right URL with client_id and scopes in the body`` () =
    let body =
        """{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":5}"""

    let transport = QueuedTransport [ ok 200 body ]

    let _ =
        DeviceFlow.beginAsync transport cfg nowFixed ct |> _.GetAwaiter().GetResult()

    let req = transport.Recorded.[0]
    Assert.Equal("POST", req.Method)
    Assert.Equal("https://github.com/login/device/code", req.Url)
    Assert.Contains(("client_id", "Iv1.testclient"), req.Body)
    Assert.Contains(("scope", "repo,read:user"), req.Body)

[<Fact>]
let ``beginAsync omits scope param when no scopes requested`` () =
    let body =
        """{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":5}"""

    let transport = QueuedTransport [ ok 200 body ]

    let cfgNoScopes =
        { cfg with
            Scopes = Array.empty
            Method = AuthMethod.GitHubApp }

    let _ =
        DeviceFlow.beginAsync transport cfgNoScopes nowFixed ct
        |> _.GetAwaiter().GetResult()

    let req = transport.Recorded.[0]
    Assert.DoesNotContain(req.Body, (fun (k, _) -> k = "scope"))

[<Fact>]
let ``beginAsync surfaces server 4xx as External`` () =
    let transport = QueuedTransport [ ok 400 "bad" ]

    let result =
        DeviceFlow.beginAsync transport cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("400", msg)
    | other -> Assert.Fail $"Expected External (github), got {other}"

[<Fact>]
let ``beginAsync surfaces missing device_code as External`` () =
    let body =
        """{"device_code":"","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":5}"""

    let transport = QueuedTransport [ ok 200 body ]

    let result =
        DeviceFlow.beginAsync transport cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("device_code", msg)
    | other -> Assert.Fail $"Expected External (github), got {other}"

[<Fact>]
let ``beginAsync surfaces transport failure`` () =
    let transport = QueuedTransport [ Failure(AveliaError.Network "DNS down") ]

    let result =
        DeviceFlow.beginAsync transport cfg nowFixed ct |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.Network "DNS down") -> ()
    | other -> Assert.Fail $"Expected Network failure, got {other}"

// ----------------------------------------------------------------------------
//  classifyPoll — pure state-machine unit tests
// ----------------------------------------------------------------------------

let private mkDto (error: string) (token: string) =
    let d = AccessTokenResponse()
    d.error <- error
    d.access_token <- token
    d

[<Fact>]
let ``classifyPoll handles authorization_pending as KeepPolling`` () =
    let dto = mkDto "authorization_pending" ""
    let interval = TimeSpan.FromSeconds 5.0

    let step = DeviceFlow.classifyPollForTesting cfg nowFixed interval dto

    match step with
    | DeviceFlow.PollStep.KeepPolling d -> Assert.Equal(interval, d)
    | other -> Assert.Fail $"Expected KeepPolling, got {other}"

[<Fact>]
let ``classifyPoll handles slow_down by bumping the interval by 5s`` () =
    let dto = mkDto "slow_down" ""
    let interval = TimeSpan.FromSeconds 5.0
    let step = DeviceFlow.classifyPollForTesting cfg nowFixed interval dto

    match step with
    | DeviceFlow.PollStep.SlowDown ni -> Assert.Equal(TimeSpan.FromSeconds 10.0, ni)
    | other -> Assert.Fail $"Expected SlowDown, got {other}"

[<Fact>]
let ``classifyPoll handles expired_token as External failure`` () =
    let dto = mkDto "expired_token" ""

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Failed(AveliaError.External("github", msg)) -> Assert.Contains("expired", msg)
    | other -> Assert.Fail $"Expected Failed (External), got {other}"

[<Fact>]
let ``classifyPoll handles access_denied as Unauthorized`` () =
    let dto = mkDto "access_denied" ""

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Failed AveliaError.Unauthorized -> ()
    | other -> Assert.Fail $"Expected Failed (Unauthorized), got {other}"

[<Fact>]
let ``classifyPoll handles unknown error with description`` () =
    let dto = mkDto "weird_error" ""
    dto.error_description <- "the server is sad"

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Failed(AveliaError.External("github", msg)) ->
        Assert.Contains("weird_error", msg)
        Assert.Contains("server is sad", msg)
    | other -> Assert.Fail $"Expected Failed (External), got {other}"

[<Fact>]
let ``classifyPoll parses an Acquired token with scopes`` () =
    let dto = mkDto "" "ghu_tokenvalue"
    dto.scope <- "repo,read:user"
    dto.expires_in <- 3600

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Acquired t ->
        Assert.Equal("ghu_tokenvalue", t.Token)
        Assert.Equal(AuthMethod.OAuthApp, t.Method)
        Assert.Equal<string[]>([| "repo"; "read:user" |], t.ScopesGranted)
        Assert.Equal(nowFixed().AddSeconds 3600.0, t.ExpiresAt)
        // Account stays empty until resolved via GET /user.
        Assert.Equal("", t.Account)
    | other -> Assert.Fail $"Expected Acquired, got {other}"

[<Fact>]
let ``classifyPoll Acquired with no expires_in uses MaxValue`` () =
    let dto = mkDto "" "ghp_pat"

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Acquired t -> Assert.Equal(DateTimeOffset.MaxValue, t.ExpiresAt)
    | other -> Assert.Fail $"Expected Acquired, got {other}"

[<Fact>]
let ``classifyPoll Acquired with empty access_token surfaces as Failed`` () =
    let dto = mkDto "" ""

    let step =
        DeviceFlow.classifyPollForTesting cfg nowFixed (TimeSpan.FromSeconds 5.0) dto

    match step with
    | DeviceFlow.PollStep.Failed(AveliaError.External("github", msg)) -> Assert.Contains("access_token", msg)
    | other -> Assert.Fail $"Expected Failed (External), got {other}"

// ----------------------------------------------------------------------------
//  completeAsync — full polling loop
// ----------------------------------------------------------------------------

[<Fact>]
let ``completeAsync polls through pending then success`` () =
    let pendingBody = """{"error":"authorization_pending"}"""

    let successBody =
        """{"access_token":"ghu_token","token_type":"bearer","scope":"repo,read:user","expires_in":28800}"""

    let transport = QueuedTransport [ ok 200 pendingBody; ok 200 successBody ]

    let challenge: DeviceCodeChallenge =
        { UserCode = "WDJB-MJHT"
          VerificationUri = "https://github.com/login/device"
          VerificationUriComplete = ""
          DeviceCode = "device-abc"
          Interval = TimeSpan.FromSeconds 1.0
          ExpiresAt = nowFixed().AddMinutes 15.0 }

    let result =
        DeviceFlow.completeAsync transport cfg challenge nowFixed noDelay ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Success t ->
        Assert.Equal("ghu_token", t.Token)
        Assert.Equal<string[]>([| "repo"; "read:user" |], t.ScopesGranted)
        Assert.Equal(2, transport.Recorded.Count)
        // Each poll posts to the token endpoint with the device code.
        for req in transport.Recorded do
            Assert.Equal("POST", req.Method)
            Assert.Equal("https://github.com/login/oauth/access_token", req.Url)
            Assert.Contains(("device_code", "device-abc"), req.Body)

            Assert.Contains(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), req.Body)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``completeAsync surfaces access_denied as Unauthorized`` () =
    let body = """{"error":"access_denied"}"""
    let transport = QueuedTransport [ ok 200 body ]

    let challenge: DeviceCodeChallenge =
        { UserCode = "X"
          VerificationUri = ""
          VerificationUriComplete = ""
          DeviceCode = "d"
          Interval = TimeSpan.FromMilliseconds 0.0
          ExpiresAt = nowFixed().AddMinutes 15.0 }

    let result =
        DeviceFlow.completeAsync transport cfg challenge nowFixed noDelay ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

[<Fact>]
let ``completeAsync fails locally when the challenge has expired`` () =
    let transport = QueuedTransport []

    let challenge: DeviceCodeChallenge =
        { UserCode = "X"
          VerificationUri = ""
          VerificationUriComplete = ""
          DeviceCode = "d"
          Interval = TimeSpan.FromMilliseconds 0.0
          // Already expired against nowFixed().
          ExpiresAt = nowFixed().AddMinutes(-1.0) }

    let result =
        DeviceFlow.completeAsync transport cfg challenge nowFixed noDelay ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("expired", msg)
    | other -> Assert.Fail $"Expected External (expired), got {other}"

[<Fact>]
let ``completeAsync slow_down bumps the polling interval`` () =
    // Sequence: slow_down -> success. Capture each delay request.
    let slowBody = """{"error":"slow_down"}"""
    let successBody = """{"access_token":"tok","scope":"repo","expires_in":60}"""
    let transport = QueuedTransport [ ok 200 slowBody; ok 200 successBody ]
    let delays = ResizeArray<TimeSpan>()

    let recordingDelay (t: TimeSpan) (_: CancellationToken) =
        delays.Add t
        Task.CompletedTask

    let challenge: DeviceCodeChallenge =
        { UserCode = "X"
          VerificationUri = ""
          VerificationUriComplete = ""
          DeviceCode = "d"
          Interval = TimeSpan.FromSeconds 5.0
          ExpiresAt = nowFixed().AddMinutes 15.0 }

    let result =
        DeviceFlow.completeAsync transport cfg challenge nowFixed recordingDelay ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Success _ ->
        Assert.Equal(2, delays.Count)
        Assert.Equal(TimeSpan.FromSeconds 5.0, delays.[0])
        // Bumped by +5s per RFC 8628 §3.5.
        Assert.Equal(TimeSpan.FromSeconds 10.0, delays.[1])
    | Failure e -> Assert.Fail $"Expected success: {e}"

// ----------------------------------------------------------------------------
//  PAT validation (resolveLoginAsync)
// ----------------------------------------------------------------------------

[<Fact>]
let ``resolveLoginAsync 200 returns the login`` () =
    let body = """{"login":"octocat"}"""
    let transport = QueuedTransport [ ok 200 body ]

    let result =
        DeviceFlow.resolveLoginAsync transport cfg "pat-abc" ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Success "octocat" -> ()
    | other -> Assert.Fail $"Expected Success 'octocat', got {other}"

[<Fact>]
let ``resolveLoginAsync 401 returns Unauthorized`` () =
    let transport = QueuedTransport [ ok 401 """{"message":"Bad credentials"}""" ]

    let result =
        DeviceFlow.resolveLoginAsync transport cfg "pat-bad" ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

[<Fact>]
let ``resolveLoginAsync uses api.github.com for the public host`` () =
    let body = """{"login":"x"}"""
    let transport = QueuedTransport [ ok 200 body ]

    let _ =
        DeviceFlow.resolveLoginAsync transport cfg "tok" ct
        |> _.GetAwaiter().GetResult()

    let req = transport.Recorded.[0]
    Assert.Equal("GET", req.Method)
    Assert.Equal("https://api.github.com/user", req.Url)
    Assert.Equal("tok", req.Bearer)

[<Fact>]
let ``resolveLoginAsync uses /api/v3 for GHES hosts`` () =
    let body = """{"login":"x"}"""
    let transport = QueuedTransport [ ok 200 body ]

    let ghesCfg =
        { cfg with
            Host = "https://ghe.example.com" }

    let _ =
        DeviceFlow.resolveLoginAsync transport ghesCfg "tok" ct
        |> _.GetAwaiter().GetResult()

    Assert.Equal("https://ghe.example.com/api/v3/user", transport.Recorded.[0].Url)

[<Fact>]
let ``resolveLoginAsync 5xx returns External`` () =
    let transport = QueuedTransport [ ok 503 "down" ]

    let result =
        DeviceFlow.resolveLoginAsync transport cfg "tok" ct
        |> _.GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", msg)) -> Assert.Contains("503", msg)
    | other -> Assert.Fail $"Expected External, got {other}"
