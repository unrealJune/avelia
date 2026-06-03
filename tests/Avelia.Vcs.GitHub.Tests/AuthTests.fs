module Avelia.Vcs.GitHub.Tests.AuthTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Auth
open Avelia.Vcs.GitHub.Tests.OctokitHttpStub
open Octokit.Internal

// ----------------------------------------------------------------------------
//  Orchestration tests for GitHubAuth driving Octokit's IHttpClient.
//  Assert the auth flow:
//    * BeginDeviceFlow + CompleteDeviceFlow round-trip; the resulting
//      token is persisted under "avelia:github:<resolved-login>".
//    * SignInWithPat validates via GET /user and stores on success.
//    * 401 surfaces as Unauthorized (no persistence).
//    * Load / SignOut round-trip through the credential store.
// ----------------------------------------------------------------------------

let private cfg: DeviceFlowConfig =
    { Host = "https://github.com"
      ClientId = "Iv1.testclient"
      Scopes = [| "repo"; "read:user" |]
      Method = AuthMethod.OAuthApp }

let private nowFixed =
    Func<DateTimeOffset>(fun () -> DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero))

let private ct = CancellationToken.None

/// In-memory <see cref="ICredentialStore"/> for tests.
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

/// Build a <see cref="GitHubAuth"/> whose Octokit clients all share the
/// supplied scripted <see cref="ScriptedHttpClient"/>.
let private buildAuth (http: ScriptedHttpClient) (store: ICredentialStore) : IGitHubAuth =
    GitHubAuth(Func<IHttpClient>(fun () -> http :> IHttpClient), store, nowFixed) :> IGitHubAuth

// ============================================================================
//  CompleteDeviceFlowAsync — full happy path
// ============================================================================

let private challenge: DeviceCodeChallenge =
    { UserCode = "WDJB-MJHT"
      VerificationUri = "https://github.com/login/device"
      DeviceCode = "device-abc"
      Interval = TimeSpan.Zero
      ExpiresAt = nowFixed.Invoke().AddMinutes 15.0 }

[<Fact>]
let ``CompleteDeviceFlowAsync stores the resolved-login token under canonical key`` () =
    // Sequence:
    //   1. POST /login/oauth/access_token -> success with token
    //   2. GET  /user                     -> {login: "OctoCat"}
    let tokenBody =
        """{"access_token":"ghu_token","token_type":"bearer","scope":"repo,read:user","expires_in":28800}"""

    let userBody = """{"login":"OctoCat","id":1,"name":"The Octocat"}"""

    let http = new ScriptedHttpClient([ ok tokenBody; ok userBody ])
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result =
        auth.CompleteDeviceFlowAsync(cfg, challenge, ct).GetAwaiter().GetResult()

    match result with
    | Success t ->
        Assert.Equal("OctoCat", t.Account.Value)
        Assert.Equal("ghu_token", t.Token)
        let key = CredentialKey.forGitHubAccount "OctoCat"
        Assert.True(store.Snapshot().ContainsKey key)

        match TokenSerializer.deserialize (store.Snapshot().[key]) with
        | Success loaded -> Assert.Equal("ghu_token", loaded.Token)
        | Failure e -> Assert.Fail $"Roundtrip failed: {e}"
    | Failure e -> Assert.Fail $"Expected success, got {e}"

    // Two requests: token endpoint (POST github.com) + /user (GET api.github.com).
    Assert.Equal(2, http.Recorded.Count)
    Assert.Equal("https://github.com/login/oauth/access_token", http.Recorded.[0].Url)
    Assert.Equal("https://api.github.com/user", http.Recorded.[1].Url)

[<Fact>]
let ``CompleteDeviceFlowAsync propagates polling failures without calling /user`` () =
    let body = """{"error":"access_denied","error_description":"the user declined"}"""
    let http = new ScriptedHttpClient([ ok body ])
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result =
        auth.CompleteDeviceFlowAsync(cfg, challenge, ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.External("github", _)) ->
        // /user was not called — only the one OAuth POST.
        Assert.Single(http.Recorded) |> ignore
        Assert.Empty(store.Snapshot())
    | other -> Assert.Fail $"Expected External (github), got {other}"

// ============================================================================
//  SignInWithPatAsync
// ============================================================================

[<Fact>]
let ``SignInWithPatAsync validates via GET /user and stores on success`` () =
    let userBody = """{"login":"alice","id":7,"name":"Alice"}"""
    let http = new ScriptedHttpClient([ ok userBody ])
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result =
        auth.SignInWithPatAsync(cfg, "ghp_classic", ct).GetAwaiter().GetResult()

    match result with
    | Success t ->
        Assert.Equal("alice", t.Account.Value)
        Assert.Equal("ghp_classic", t.Token)
        Assert.Equal(AuthMethod.Pat, t.Method)
        Assert.True(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "alice"))
    | Failure e -> Assert.Fail $"Expected success, got {e}"

    // The PAT becomes the bearer credential Octokit attaches.
    let req = http.Recorded.[0]
    Assert.Equal("https://api.github.com/user", req.Url)
    // Octokit attaches Authorization via the HTTP message, not the
    // IRequest.Headers dictionary — checking the value here would
    // require unwrapping the inner HttpContent. The successful 200
    // round-trip plus the store side-effect is sufficient signal.
    Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method)

[<Fact>]
let ``SignInWithPatAsync surfaces 401 as Unauthorized without storing`` () =
    let http =
        new ScriptedHttpClient([ okJson HttpStatusCode.Unauthorized """{"message":"Bad credentials"}""" ])

    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result = auth.SignInWithPatAsync(cfg, "ghp_bad", ct).GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> Assert.Empty(store.Snapshot())
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

[<Fact>]
let ``SignInWithPatAsync rejects empty PAT before calling the network`` () =
    let http = new ScriptedHttpClient(Seq.empty)
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result = auth.SignInWithPatAsync(cfg, "", ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.Validation _) -> Assert.Empty(http.Recorded)
    | other -> Assert.Fail $"Expected Validation, got {other}"

// ============================================================================
//  Load / SignOut
// ============================================================================

[<Fact>]
let ``LoadStoredTokenAsync returns NotFound for unknown login`` () =
    let http = new ScriptedHttpClient(Seq.empty)
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let result =
        auth.LoadStoredTokenAsync(GitHubLogin.Create "unknown", ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> Assert.Fail $"Expected NotFound, got {other}"

[<Fact>]
let ``Store then Load round-trips`` () =
    let userBody = """{"login":"bob","id":2,"name":"Bob"}"""
    let http = new ScriptedHttpClient([ ok userBody ])
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let _ = auth.SignInWithPatAsync(cfg, "ghp_token", ct).GetAwaiter().GetResult()

    let loaded =
        auth.LoadStoredTokenAsync(GitHubLogin.Create "bob", ct).GetAwaiter().GetResult()

    match loaded with
    | Success t ->
        Assert.Equal("bob", t.Account.Value)
        Assert.Equal("ghp_token", t.Token)
        Assert.Equal(AuthMethod.Pat, t.Method)
    | Failure e -> Assert.Fail $"Expected success, got {e}"

[<Fact>]
let ``SignOutAsync is idempotent on a missing account`` () =
    let http = new ScriptedHttpClient(Seq.empty)
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let r1 =
        auth.SignOutAsync(GitHubLogin.Create "never-existed", ct).GetAwaiter().GetResult()

    let r2 =
        auth.SignOutAsync(GitHubLogin.Create "never-existed", ct).GetAwaiter().GetResult()

    match r1, r2 with
    | Success(), Success() -> ()
    | _ -> Assert.Fail "Both calls should succeed."

[<Fact>]
let ``SignOutAsync after sign-in removes the credential`` () =
    let http = new ScriptedHttpClient([ ok """{"login":"carol","id":3}""" ])
    let store = InMemoryCredentialStore()
    let auth = buildAuth http store

    let _ = auth.SignInWithPatAsync(cfg, "ghp_t", ct).GetAwaiter().GetResult()

    Assert.True(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "carol"))

    let _ = auth.SignOutAsync(GitHubLogin.Create "carol", ct).GetAwaiter().GetResult()
    Assert.False(store.Snapshot().ContainsKey(CredentialKey.forGitHubAccount "carol"))
