module Avelia.Vcs.GitHub.Tests.ApiClientTests

open System
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Tests.OctokitHttpStub
open Octokit.Internal

// ----------------------------------------------------------------------------
//  GitHubClient driven through Octokit's full pipeline with a stubbed
//  IHttpClient. The tests assert:
//    * The right REST endpoint is hit with the right method + body.
//    * Octokit's deserializer maps successful responses to our
//      RepoSummary / PullRequest / Notification shapes correctly.
//    * Errors surface as the corresponding AveliaError case.
// ----------------------------------------------------------------------------

let private ct = CancellationToken.None

let private buildOctokitClient (http: ScriptedHttpClient) : Octokit.GitHubClient =
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
            SimpleJsonSerializer()
        )

    Octokit.GitHubClient conn

let private buildClient (http: ScriptedHttpClient) : IGitHubClient =
    let cache = InMemoryResponseCache() :> Octokit.Caching.IResponseCache
    GitHubClient(buildOctokitClient http, cache) :> IGitHubClient

// ============================================================================
//  ListUserReposAsync
// ============================================================================

[<Fact>]
let ``ListUserReposAsync maps a one-page response to RepoSummary`` () =
    let body =
        """[{"id":1,"name":"avelia","default_branch":"main","private":false,"owner":{"login":"unrealJune"},"clone_url":"https://github.com/unrealJune/avelia.git"}]"""
    // Empty next-Link header so Octokit stops paginating.
    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result = client.ListUserReposAsync(ct).GetAwaiter().GetResult()

    match result with
    | Success repos ->
        Assert.Single(repos) |> ignore
        let r = repos.[0]
        Assert.Equal("unrealJune", r.Owner)
        Assert.Equal("avelia", r.Name)
        Assert.Equal("main", r.DefaultBranch.Value)
        Assert.False r.IsPrivate
        Assert.Equal("https://github.com/unrealJune/avelia.git", r.CloneUrl)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``ListUserReposAsync targets the /user/repos endpoint`` () =
    let http = new ScriptedHttpClient([ ok "[]" ])
    let client = buildClient http

    let _ = client.ListUserReposAsync(ct).GetAwaiter().GetResult()

    let req = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method)
    Assert.Contains("/user/repos", req.Url)

// ============================================================================
//  GetPullRequestAsync
// ============================================================================

[<Fact>]
let ``GetPullRequestAsync maps a successful response`` () =
    let body =
        """{
          "number": 42,
          "title": "Add the thing",
          "state": "open",
          "draft": false,
          "merged": false,
          "head": { "ref": "feature/x" },
          "base": { "ref": "main" },
          "mergeable": true
        }"""

    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http
    let repo = { Owner = "owner"; Name = "repo" }

    let result = client.GetPullRequestAsync(repo, 42, ct).GetAwaiter().GetResult()

    match result with
    | Success pr ->
        Assert.Equal(42, pr.Number)
        Assert.Equal("Add the thing", pr.Title)
        Assert.Equal("feature/x", pr.Branch.Value)
        Assert.Equal("main", pr.Base.Value)
        Assert.Equal(PrStatus.Open, pr.Status)
        Assert.True pr.MergeReady
    | Failure e -> Assert.Fail $"Expected success: {e}"

    let req = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method)
    Assert.Contains("/repos/owner/repo/pulls/42", req.Url)

[<Fact>]
let ``GetPullRequestAsync 404 returns NotFound`` () =
    let http =
        new ScriptedHttpClient([ okJson HttpStatusCode.NotFound """{"message":"Not Found"}""" ])

    let client = buildClient http

    let repo = { Owner = "o"; Name = "r" }
    let result = client.GetPullRequestAsync(repo, 99, ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> Assert.Fail $"Expected NotFound, got {other}"

[<Fact>]
let ``GetPullRequestAsync 401 returns Unauthorized`` () =
    let http =
        new ScriptedHttpClient([ okJson HttpStatusCode.Unauthorized """{"message":"Bad credentials"}""" ])

    let client = buildClient http
    let repo = { Owner = "o"; Name = "r" }
    let result = client.GetPullRequestAsync(repo, 1, ct).GetAwaiter().GetResult()

    match result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> Assert.Fail $"Expected Unauthorized, got {other}"

// ============================================================================
//  CreatePullRequestAsync
// ============================================================================

[<Fact>]
let ``CreatePullRequestAsync posts the right body`` () =
    let body =
        """{
          "number": 7,
          "title": "T",
          "state": "open",
          "draft": true,
          "merged": false,
          "head": { "ref": "b" },
          "base": { "ref": "main" },
          "mergeable": null
        }"""

    let http = new ScriptedHttpClient([ okJson HttpStatusCode.Created body ])
    let client = buildClient http

    let req: CreatePrRequest =
        { Repo = { Owner = "o"; Name = "r" }
          Title = "T"
          Body = "B"
          Head = BranchName.Create "b"
          Base = BranchName.Create "main"
          Draft = true }

    let result = client.CreatePullRequestAsync(req, ct).GetAwaiter().GetResult()

    match result with
    | Success pr ->
        Assert.Equal(7, pr.Number)
        Assert.Equal(PrStatus.Draft, pr.Status)
    | Failure e -> Assert.Fail $"Expected success: {e}"

    let recorded = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Post, recorded.Method)
    Assert.Contains("/repos/o/r/pulls", recorded.Url)

[<Fact>]
let ``CreatePullRequestAsync 422 maps to Validation`` () =
    let http =
        new ScriptedHttpClient(
            [ okJson HttpStatusCode.UnprocessableEntity """{"message":"Validation Failed","errors":[]}""" ]
        )

    let client = buildClient http

    let req: CreatePrRequest =
        { Repo = { Owner = "o"; Name = "r" }
          Title = "T"
          Body = ""
          Head = BranchName.Create "b"
          Base = BranchName.Create "main"
          Draft = false }

    let result = client.CreatePullRequestAsync(req, ct).GetAwaiter().GetResult()

    match result with
    | Failure(AveliaError.Validation _) -> ()
    | other -> Assert.Fail $"Expected Validation, got {other}"

// ============================================================================
//  CommentAsync
// ============================================================================

[<Fact>]
let ``CommentAsync posts to the issues/{n}/comments endpoint`` () =
    let body = """{"id":111,"body":"hello"}"""
    let http = new ScriptedHttpClient([ okJson HttpStatusCode.Created body ])
    let client = buildClient http
    let repo = { Owner = "o"; Name = "r" }

    let result = client.CommentAsync(repo, 42, "hello", ct).GetAwaiter().GetResult()

    match result with
    | Success() -> ()
    | Failure e -> Assert.Fail $"Expected success: {e}"

    let req = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Post, req.Method)
    Assert.Contains("/repos/o/r/issues/42/comments", req.Url)

// ============================================================================
//  ListNotificationsAsync
// ============================================================================

[<Fact>]
let ``ListNotificationsAsync maps response to our Notification shape`` () =
    let body =
        """[{
          "id":"abc",
          "repository":{"full_name":"o/r"},
          "subject":{"title":"PR merged","type":"PullRequest"},
          "reason":"subject_merged",
          "updated_at":"2026-05-18T12:00:00Z",
          "url":"https://api.github.com/notifications/threads/abc"
        }]"""

    let http = new ScriptedHttpClient([ ok body ])
    let client = buildClient http

    let result =
        client.ListNotificationsAsync(DateTimeOffset.MinValue, ct).GetAwaiter().GetResult()

    match result with
    | Success notifs ->
        Assert.Single(notifs) |> ignore
        let n = notifs.[0]
        Assert.Equal("abc", n.Id)
        Assert.Equal("o/r", n.RepoFullName)
        Assert.Equal("PR merged", n.Subject)
        Assert.Equal("subject_merged", n.Reason)
        Assert.Equal(DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero), n.UpdatedAt)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``ListNotificationsAsync hits /notifications`` () =
    let http = new ScriptedHttpClient([ ok "[]" ])
    let client = buildClient http

    let _ =
        client.ListNotificationsAsync(DateTimeOffset.MinValue, ct).GetAwaiter().GetResult()

    let req = http.Recorded.[0]
    Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method)
    Assert.Contains("/notifications", req.Url)

// ============================================================================
//  LastRateLimit
// ============================================================================

[<Fact>]
let ``LastRateLimit captures the rate-limit headers on a response`` () =
    let body = "[]"

    let headers =
        [ "X-RateLimit-Limit", "5000"
          "X-RateLimit-Remaining", "4321"
          "X-RateLimit-Reset", "1737504000" // 2025-01-21 hard-coded for test stability
          "ETag", "\"abc\"" ]

    let http = new ScriptedHttpClient([ okWithHeaders HttpStatusCode.OK body headers ])
    let client = buildClient http

    let _ = client.ListUserReposAsync(ct).GetAwaiter().GetResult()

    match client.LastRateLimit with
    | ValueSome s ->
        Assert.Equal(5000, s.Limit)
        Assert.Equal(4321, s.Remaining)
    | ValueNone -> Assert.Fail "Expected a captured snapshot"

[<Fact>]
let ``LastRateLimit stays ValueNone before the first call`` () =
    let http = new ScriptedHttpClient(Seq.empty)
    let client = buildClient http
    Assert.Equal(ValueNone, client.LastRateLimit)
