module Avelia.Vcs.GitHub.Tests.DashboardQueryTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Tests.OctokitHttpStub
open Octokit.Internal

// ----------------------------------------------------------------------------
//  B-5 — GraphQL dashboard query.
//
//  The whole path (payload build → IConnection.Run → envelope parse →
//  PullRequest mapping) is exercised through a stub
//  Octokit.GraphQL.IConnection that returns canned response JSON. Because
//  we own the query text, we also own the response field names, so
//  hand-written JSON is faithful to what GitHub returns for this query —
//  no live endpoint needed.
// ----------------------------------------------------------------------------

let private ct = CancellationToken.None

/// Stub IConnection: records the payload it was handed and replies with
/// whatever the responder produces (canned JSON, or a thrown exception).
type private StubConnection(responder: string -> string) =
    member val LastPayload = "" with get, set

    interface Octokit.GraphQL.IConnection with
        member _.Uri = Uri("https://api.github.com/graphql")

        member this.Run(query: string, _ct: CancellationToken) : Task<string> =
            this.LastPayload <- query
            Task.FromResult(responder query)

let private runDashboard (json: string) : OperationResult<System.Collections.Generic.IReadOnlyList<PullRequest>> =
    let conn = StubConnection(fun _ -> json)

    let query =
        OctokitGraphQlDashboardQuery(conn :> Octokit.GraphQL.IConnection) :> IDashboardQuery

    query.ListViewerPullRequestsAsync(ct).GetAwaiter().GetResult()

// A complete viewer→pullRequests→nodes envelope with one PR carrying two
// check runs (one completed-success, one still in progress).
let private oneOpenPrJson =
    """
    {
      "data": {
        "viewer": {
          "pullRequests": {
            "nodes": [
              {
                "number": 42,
                "title": "Add the thing",
                "isDraft": false,
                "mergeable": "MERGEABLE",
                "state": "OPEN",
                "headRefName": "feature/x",
                "baseRefName": "main",
                "reviewDecision": "APPROVED",
                "commits": {
                  "nodes": [
                    {
                      "commit": {
                        "statusCheckRollup": { "state": "SUCCESS" },
                        "checkSuites": {
                          "nodes": [
                            {
                              "checkRuns": {
                                "nodes": [
                                  { "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS" },
                                  { "name": "test", "status": "IN_PROGRESS", "conclusion": null }
                                ]
                              }
                            }
                          ]
                        }
                      }
                    }
                  ]
                }
              }
            ]
          }
        }
      }
    }
    """

// ============================================================================
//  Mapping (the testable core, driven through the public query)
// ============================================================================

[<Fact>]
let ``maps a full PR with its checks`` () =
    match runDashboard oneOpenPrJson with
    | Success prs ->
        Assert.Single prs |> ignore
        let pr = prs.[0]
        Assert.Equal(42, pr.Number)
        let (PullRequestId idValue) = pr.Id
        Assert.Equal(42, idValue)
        Assert.Equal("Add the thing", pr.Title)
        Assert.Equal("feature/x", pr.Branch.Value)
        Assert.Equal("main", pr.Base.Value)
        Assert.Equal(PrStatus.Approved, pr.Status)
        Assert.True pr.MergeReady
        Assert.Equal(2, pr.Checks.Length)
        Assert.Equal("build", pr.Checks.[0].Name)
        Assert.Equal(CheckStatus.Passed, pr.Checks.[0].Status)
        Assert.Equal("test", pr.Checks.[1].Name)
        // Not COMPLETED → Running, regardless of the null conclusion.
        Assert.Equal(CheckStatus.Running, pr.Checks.[1].Status)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``the payload carries the viewer pullRequests query`` () =
    let conn = StubConnection(fun _ -> oneOpenPrJson)

    let query =
        OctokitGraphQlDashboardQuery(conn :> Octokit.GraphQL.IConnection) :> IDashboardQuery

    query.ListViewerPullRequestsAsync(ct).GetAwaiter().GetResult() |> ignore

    Assert.Contains("viewer", conn.LastPayload)
    Assert.Contains("pullRequests", conn.LastPayload)
    Assert.Contains("checkRuns", conn.LastPayload)

[<Theory>]
[<InlineData("CHANGES_REQUESTED", false, "InReview")>]
[<InlineData("REVIEW_REQUIRED", false, "InReview")>]
[<InlineData("APPROVED", false, "Approved")>]
[<InlineData("", false, "Open")>]
[<InlineData("", true, "Draft")>]
let ``review decision and draft drive PrStatus`` (decision: string) (isDraft: bool) (expected: string) =
    let json =
        $"""
        {{ "data": {{ "viewer": {{ "pullRequests": {{ "nodes": [
          {{ "number": 1, "title": "t", "isDraft": {(if isDraft then "true" else "false")},
             "mergeable": "UNKNOWN", "state": "OPEN", "headRefName": "h", "baseRefName": "main",
             "reviewDecision": "{decision}" }}
        ] }} }} }} }}
        """

    match runDashboard json with
    | Success prs ->
        let actual =
            prs.[0].Status.ToString().Replace("PrStatus.", "").Replace("Avelia.Core.Abstractions.", "")

        Assert.Equal(expected, actual)
        Assert.False prs.[0].MergeReady // mergeable "UNKNOWN"
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Theory>]
[<InlineData("COMPLETED", "FAILURE", "Failed")>]
[<InlineData("COMPLETED", "TIMED_OUT", "Failed")>]
[<InlineData("COMPLETED", "CANCELLED", "Failed")>]
[<InlineData("COMPLETED", "SKIPPED", "Skipped")>]
[<InlineData("COMPLETED", "NEUTRAL", "Skipped")>]
[<InlineData("COMPLETED", "ACTION_REQUIRED", "Warn")>]
[<InlineData("COMPLETED", "SOMETHING_NEW", "Warn")>]
[<InlineData("QUEUED", "", "Running")>]
let ``check status maps from status + conclusion`` (status: string) (conclusion: string) (expected: string) =
    let json =
        $"""
        {{ "data": {{ "viewer": {{ "pullRequests": {{ "nodes": [
          {{ "number": 1, "title": "t", "isDraft": false, "mergeable": "MERGEABLE",
             "state": "OPEN", "headRefName": "h", "baseRefName": "main", "reviewDecision": "",
             "commits": {{ "nodes": [ {{ "commit": {{
               "checkSuites": {{ "nodes": [ {{ "checkRuns": {{ "nodes": [
                 {{ "name": "ci", "status": "{status}", "conclusion": "{conclusion}" }}
               ] }} }} ] }}
             }} }} ] }}
          }}
        ] }} }} }} }}
        """

    match runDashboard json with
    | Success prs ->
        let actual = prs.[0].Checks.[0].Status.ToString().Replace("CheckStatus.", "")
        Assert.Equal(expected, actual)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``PR with no commits yet has empty checks`` () =
    let json =
        """{ "data": { "viewer": { "pullRequests": { "nodes": [
          { "number": 5, "title": "fresh", "isDraft": false, "mergeable": "MERGEABLE",
            "state": "OPEN", "headRefName": "h", "baseRefName": "main", "reviewDecision": "",
            "commits": { "nodes": [] } }
        ] } } } }"""

    match runDashboard json with
    | Success prs -> Assert.Empty prs.[0].Checks
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``empty nodes yields an empty list`` () =
    let json = """{ "data": { "viewer": { "pullRequests": { "nodes": [] } } } }"""

    match runDashboard json with
    | Success prs -> Assert.Empty prs
    | Failure e -> Assert.Fail $"Expected success: {e}"

// ============================================================================
//  Failure surfaces
// ============================================================================

[<Fact>]
let ``a GraphQL errors envelope maps to External github-graphql`` () =
    let json =
        """{ "errors": [ { "message": "Field 'bogus' doesn't exist", "type": "FIELD_ERROR" } ] }"""

    match runDashboard json with
    | Failure(AveliaError.External("github-graphql", msg)) -> Assert.Contains("bogus", msg)
    | other -> Assert.Fail $"Expected External github-graphql, got {other}"

[<Fact>]
let ``malformed JSON maps to External github-graphql`` () =
    match runDashboard "{ not json " with
    | Failure(AveliaError.External("github-graphql", _)) -> ()
    | other -> Assert.Fail $"Expected External github-graphql, got {other}"

[<Fact>]
let ``cancellation propagates rather than collapsing to Failure`` () =
    let conn = StubConnection(fun _ -> raise (OperationCanceledException()))

    let query =
        OctokitGraphQlDashboardQuery(conn :> Octokit.GraphQL.IConnection) :> IDashboardQuery

    Assert.ThrowsAny<OperationCanceledException>(fun () ->
        query.ListViewerPullRequestsAsync(ct).GetAwaiter().GetResult() |> ignore)
    |> ignore

[<Fact>]
let ``a transport HttpRequestException maps to Network`` () =
    let conn =
        StubConnection(fun _ -> raise (System.Net.Http.HttpRequestException "connection reset"))

    let query =
        OctokitGraphQlDashboardQuery(conn :> Octokit.GraphQL.IConnection) :> IDashboardQuery

    match query.ListViewerPullRequestsAsync(ct).GetAwaiter().GetResult() with
    | Failure(AveliaError.Network _) -> ()
    | other -> Assert.Fail $"Expected Network, got {other}"

// ============================================================================
//  GitHubClient delegation
// ============================================================================

type private StubDashboard(result: OperationResult<System.Collections.Generic.IReadOnlyList<PullRequest>>) =
    member val Calls = 0 with get, set

    interface IDashboardQuery with
        member this.ListViewerPullRequestsAsync(_ct: CancellationToken) =
            this.Calls <- this.Calls + 1
            Task.FromResult result

let private buildOctokit () : Octokit.GitHubClient =
    let creds = Octokit.Credentials.Anonymous

    let store =
        { new Octokit.ICredentialStore with
            member _.GetCredentials() = Task.FromResult creds }

    let conn =
        Octokit.Connection(
            Octokit.ProductHeaderValue("Avelia", "0.1"),
            Octokit.GitHubClient.GitHubApiUrl,
            store,
            new ScriptedHttpClient(Seq.empty),
            SimpleJsonSerializer()
        )

    Octokit.GitHubClient conn

[<Fact>]
let ``GitHubClient.ListPrsForUserAsync delegates to the dashboard query`` () =
    let sample: PullRequest =
        { Id = PullRequestId 9
          Number = 9
          Title = "delegated"
          Branch = BranchName.Create "b"
          Base = BranchName.Create "main"
          Status = PrStatus.Open
          Checks = Array.empty
          MergeReady = false }

    let stub =
        StubDashboard(Success([| sample |] :> System.Collections.Generic.IReadOnlyList<_>))

    let cache = InMemoryResponseCache() :> Octokit.Caching.IResponseCache

    let client =
        GitHubClient(buildOctokit (), cache, stub :> IDashboardQuery) :> IGitHubClient

    match client.ListPrsForUserAsync(ct).GetAwaiter().GetResult() with
    | Success prs ->
        Assert.Single prs |> ignore
        Assert.Equal("delegated", prs.[0].Title)
        Assert.Equal(1, stub.Calls)
    | Failure e -> Assert.Fail $"Expected success: {e}"

[<Fact>]
let ``GitHubClient built without a GraphQL connection reports unavailable`` () =
    let cache = InMemoryResponseCache() :> Octokit.Caching.IResponseCache
    // The 2-arg test ctor wires DashboardQuery.unavailable.
    let client = GitHubClient(buildOctokit (), cache) :> IGitHubClient

    match client.ListPrsForUserAsync(ct).GetAwaiter().GetResult() with
    | Failure(AveliaError.External("github-graphql", msg)) -> Assert.Contains("not configured", msg)
    | other -> Assert.Fail $"Expected unavailable, got {other}"
