module Avelia.Vcs.GitHub.Tests.AuthTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub.Auth

// ----------------------------------------------------------------------------
//  Orchestration tests for GitHubAuth — assert that the IGitHubAuth interface
//  composes DeviceFlow + TokenStore correctly:
//    * BeginDeviceFlowAsync round-trips through the transport.
//    * CompleteDeviceFlowAsync calls /user to resolve the login and persists
//      the token under "avelia:github:<login>".
//    * SignInWithPatAsync validates via /user, fails on Unauthorized, stores
//      on success.
//    * LoadStoredTokenAsync / SignOutAsync round-trip through the store.
// ----------------------------------------------------------------------------

let private emptyHeaders: IReadOnlyDictionary<string, string> =
    Dictionary<string, string>() :> IReadOnlyDictionary<_, _>

/// In-memory <see cref="ICredentialStore"/> for tests. Matches the production
/// behaviour contract: missing-key Get → NotFound; missing-key Delete →
/// success.
type private InMemoryCredentialStore() =
    let map = ConcurrentDictionary<string, string>()

    member _.Snapshot() =
        map :> IReadOnlyDictionary<string, string>

    interface ICredentialStore with
        member _.GetAsync(key, _ct) =
            match map.TryGetValue key with
            | true, v -> Task.FromResult(Success v)
            | _ -> Task.FromResult(Failure(AveliaError.NotFound $"credential:{key}"))

        member _.SetAsync(key, secret, _ct) =
            map.[key] <- secret
            Task.FromResult(Success())

        member _.DeleteAsync(key, _ct) =
            map.TryRemove key |> ignore
            Task.FromResult(Success())

type private RecordedRequest =
    { Method: string
      Url: string
      Bearer: string
      Body: (string * string) list }

type private QueuedTransport(responses: OperationResult<HttpResponse> seq) =
    let queue = Queue(responses)
    let recorded = ResizeArray<RecordedRequest>()
    member _.Recorded = recorded :> IReadOnlyList<_>

    interface IHttpTransport with
        member _.SendAsync(method, url, bearerToken, formBody, _ct) =
            recorded.Add
                { Method = method
                  Url = url
                  Bearer = bearerToken
                  Body = formBody |> Seq.toList }

            if queue.Count = 0 then
                Task.FromResult(Failure(AveliaError.Internal "No more scripted responses."))
            else
                Task.FromResult(queue.Dequeue())

let private ok (status: int) (body: string) =
    Success
        { StatusCode = status
          Body = body
          Headers = emptyHeaders }

let private cfg: DeviceFlowConfig =
    { Host = "https://github.com"
      ClientId = "Iv1.testclient"
      Scopes = [| "repo"; "read:user" |]
      Method = AuthMethod.OAuthApp }

let private nowFixed =
    Func<DateTimeOffset>(fun () -> DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero))

let private noDelay =
    Func<TimeSpan, CancellationToken, Task>(fun _ _ -> Task.CompletedTask)

let private ct = CancellationToken.None

// ----------------------------------------------------------------------------
//  CompleteDeviceFlowAsync — happy path
// ----------------------------------------------------------------------------

[<Fact>]
let ``CompleteDeviceFlowAsync stores the resolved-login token under canonical key`` () =
    // Sequence:
    //   1. POST /login/oauth/access_token -> success with token
    //   2. GET /user -> {login: "octocat"}
    let tokenBody =
        """{"access_token":"ghu_token","token_type":"bearer","scope":"repo,read:user","expires_in":28800}"""

    let userBody = """{"login":"OctoCat"}"""
    let transport = QueuedTransport [ ok 200 tokenBody; ok 200 userBody ]
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let challenge: DeviceCodeChallenge =
        { UserCode = "WDJB-MJHT"
          VerificationUri = ""
          VerificationUriComplete = ""
          DeviceCode = "device-abc"
          Interval = TimeSpan.Zero
          ExpiresAt = nowFixed.Invoke().AddMinutes 15.0 }

    let result =
        auth.CompleteDeviceFlowAsync(cfg, challenge, ct).GetAwaiter().GetResult()

    match result with
    | Success t ->
        Assert.Equal("OctoCat", t.Account)
        Assert.Equal("ghu_token", t.Token)
        // Token persisted under canonical lowercased key.
        let key = CredentialKey.forGitHubAccount "OctoCat"
        Assert.True(store.Snapshot().ContainsKey key)
        // And the stored blob decodes back to the same token.
        let json = store.Snapshot().[key]

        match TokenSerializer.deserialize json with
        | Success loaded -> Assert.Equal("ghu_token", loaded.Token)
        | Failure e -> Assert.Fail $"Roundtrip failed: {e}"
    | Failure e -> Assert.Fail $"Expected success, got {e}"

[<Fact>]
let ``CompleteDeviceFlowAsync propagates polling failures without calling /user`` () =
    let body = """{"error":"access_denied"}"""
    let transport = QueuedTransport [ ok 200 body ]
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let challenge: DeviceCodeChallenge =
        { UserCode = "X"
          VerificationUri = ""
          VerificationUriComplete = ""
          DeviceCode = "d"
          Interval = TimeSpan.Zero
          ExpiresAt = nowFixed.Invoke().AddMinutes 15.0 }

    let result =
        auth.CompleteDeviceFlowAsync(cfg, challenge, ct).GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized ->
        // /user was not called.
        Assert.Single(transport.Recorded) |> ignore
        // Store is empty — nothing was persisted.
        Assert.Empty(store.Snapshot())
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

// ----------------------------------------------------------------------------
//  SignInWithPatAsync
// ----------------------------------------------------------------------------

[<Fact>]
let ``SignInWithPatAsync validates via GET /user and stores on success`` () =
    let userBody = """{"login":"alice"}"""
    let transport = QueuedTransport [ ok 200 userBody ]
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let result =
        auth.SignInWithPatAsync(cfg, "ghp_classic", ct).GetAwaiter().GetResult()

    match result with
    | Success t ->
        Assert.Equal("alice", t.Account)
        Assert.Equal("ghp_classic", t.Token)
        Assert.Equal(AuthMethod.Pat, t.Method)
        Assert.True(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "alice"))
        // PAT bearer used for the /user call.
        Assert.Equal("ghp_classic", transport.Recorded.[0].Bearer)
    | Failure e -> Assert.Fail $"Expected success, got {e}"

[<Fact>]
let ``SignInWithPatAsync surfaces 401 as Unauthorized without storing`` () =
    let transport = QueuedTransport [ ok 401 """{"message":"Bad credentials"}""" ]

    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let result = auth.SignInWithPatAsync(cfg, "ghp_bad", ct).GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> Assert.Empty(store.Snapshot())
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

[<Fact>]
let ``SignInWithPatAsync rejects empty PAT before calling the network`` () =
    let transport = QueuedTransport []
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let result = auth.SignInWithPatAsync(cfg, "", ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.Validation _) ->
        // Transport was not touched.
        Assert.Empty(transport.Recorded)
    | other -> Assert.Fail $"Expected Validation, got {other}"

// ----------------------------------------------------------------------------
//  LoadStoredTokenAsync / SignOutAsync
// ----------------------------------------------------------------------------

[<Fact>]
let ``LoadStoredTokenAsync returns NotFound for unknown login`` () =
    let transport = QueuedTransport []
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let result = auth.LoadStoredTokenAsync("unknown", ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> Assert.Fail $"Expected NotFound, got {other}"

[<Fact>]
let ``Store then Load round-trips`` () =
    let userBody = """{"login":"bob"}"""
    let transport = QueuedTransport [ ok 200 userBody ]
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let _ = auth.SignInWithPatAsync(cfg, "ghp_token", ct).GetAwaiter().GetResult()

    let loaded = auth.LoadStoredTokenAsync("bob", ct).GetAwaiter().GetResult()

    match loaded with
    | Success t ->
        Assert.Equal("bob", t.Account)
        Assert.Equal("ghp_token", t.Token)
        Assert.Equal(AuthMethod.Pat, t.Method)
    | Failure e -> Assert.Fail $"Expected success, got {e}"

[<Fact>]
let ``SignOutAsync is idempotent on a missing account`` () =
    let transport = QueuedTransport []
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let r1 = auth.SignOutAsync("never-existed", ct).GetAwaiter().GetResult()
    let r2 = auth.SignOutAsync("never-existed", ct).GetAwaiter().GetResult()

    match r1, r2 with
    | Success(), Success() -> ()
    | _ -> Assert.Fail "Both calls should succeed."

[<Fact>]
let ``SignOutAsync after sign-in removes the credential`` () =
    let transport = QueuedTransport [ ok 200 """{"login":"carol"}""" ]
    let store = InMemoryCredentialStore()
    let auth = GitHubAuth(transport, store, nowFixed, noDelay) :> IGitHubAuth

    let _ = auth.SignInWithPatAsync(cfg, "ghp_t", ct).GetAwaiter().GetResult()

    Assert.True(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "carol"))

    let _ = auth.SignOutAsync("carol", ct).GetAwaiter().GetResult()
    Assert.False(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "carol"))
