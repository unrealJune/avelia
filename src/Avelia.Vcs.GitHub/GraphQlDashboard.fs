namespace Avelia.Vcs.GitHub

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Octokit.GraphQL

// ============================================================================
//  GraphQL dashboard query (backend.md chunk B-5)
//
//  The shell's dashboard wants every open PR the user authored, each with
//  its CI checks and review state, in ONE round-trip. The REST equivalent
//  is one /pulls call + one /commits/{sha}/check-runs call + one
//  /pulls/{n}/reviews call per PR — ~76 requests for a 25-PR dashboard.
//  A single GraphQL query collapses that to one HTTP POST.
//
//  Why a raw query string instead of Octokit.GraphQL.NET's LINQ builder:
//  the builder takes `Expression<Func<...>>` and translates C#-style
//  member-init / anonymous-type expression trees. F# lambdas auto-quote to
//  System.Linq.Expressions, but F# can't express the object-initialiser
//  shape the builder expects, and anonymous records don't translate. The
//  library's `IConnection.Run(query, ct)` is a public, supported entry
//  point that posts a raw payload and hands back the response body — so we
//  own the query text (full control of field names) and parse the envelope
//  with System.Text.Json. That keeps the whole path testable against
//  hand-written JSON via a stub IConnection, with zero live endpoint.
//
//  Octokit.GraphQL still earns its dependency: Connection owns the auth
//  header, endpoint, and User-Agent plumbing the plan committed to.
// ============================================================================

/// Internal seam over the GraphQL transport so <see cref="GitHubClient"/>
/// is constructable and testable without a live GraphQL endpoint. Public
/// for the same pragmatic reason <see cref="IGitHubClient"/> is — F# has
/// no namespace-scoped visibility, and the test project injects a stub.
type IDashboardQuery =
    /// Open pull requests authored by the authenticated viewer, each with
    /// CI checks + review state folded in. Bounded by the impl's page cap;
    /// the dashboard shows the most-recently-updated first.
    abstract ListViewerPullRequestsAsync: CancellationToken -> Task<OperationResult<IReadOnlyList<PullRequest>>>

// ----------------------------------------------------------------------------
//  Pure parse + mapping — the testable core
//
//  Manual JsonDocument navigation rather than typed deserialisation: the
//  response is deeply nested with several nullable joints (a PR may have no
//  commits yet; statusCheckRollup is null until a check reports; mergeable
//  is a tri-state enum). Hand-walking the tree keeps every one of those a
//  local, total decision instead of leaning on STJ's null behaviour.
// ----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module internal DashboardMapping =

    /// Field name → child element, or ValueNone when absent or JSON null.
    let private prop (name: string) (el: JsonElement) : JsonElement voption =
        match el.ValueKind with
        | JsonValueKind.Object ->
            match el.TryGetProperty name with
            | true, child when child.ValueKind <> JsonValueKind.Null -> ValueSome child
            | _ -> ValueNone
        | _ -> ValueNone

    /// Read a string property, or "" when absent / null / not a string.
    let private str (name: string) (el: JsonElement) : string =
        match prop name el with
        | ValueSome child when child.ValueKind = JsonValueKind.String ->
            match child.GetString() with
            | null -> ""
            | s -> s
        | _ -> ""

    let private int' (name: string) (el: JsonElement) : int =
        match prop name el with
        | ValueSome child when child.ValueKind = JsonValueKind.Number ->
            match child.TryGetInt32() with
            | true, v -> v
            | _ -> 0
        | _ -> 0

    let private boolean (name: string) (el: JsonElement) : bool =
        match prop name el with
        | ValueSome child when child.ValueKind = JsonValueKind.True -> true
        | _ -> false

    /// `nodes` array under a Connection field, or empty.
    let private nodes (el: JsonElement) : JsonElement seq =
        match prop "nodes" el with
        | ValueSome arr when arr.ValueKind = JsonValueKind.Array -> arr.EnumerateArray() |> Seq.cast<JsonElement>
        | _ -> Seq.empty

    let private toBranch (s: string) : BranchName =
        match BranchName.TryCreate s with
        | Ok b -> b
        | Error _ -> Unchecked.defaultof<BranchName>

    /// GitHub's CheckStatusState / CheckConclusionState → our CheckStatus.
    /// A run that hasn't COMPLETED reports `status` (QUEUED/IN_PROGRESS/...)
    /// with a null conclusion; a completed run reports `conclusion`.
    let mapCheckStatus (status: string) (conclusion: string) : CheckStatus =
        if status <> "COMPLETED" then
            CheckStatus.Running
        else
            match conclusion with
            | "SUCCESS" -> CheckStatus.Passed
            | "FAILURE"
            | "TIMED_OUT"
            | "STARTUP_FAILURE" -> CheckStatus.Failed
            | "CANCELLED" -> CheckStatus.Failed
            | "NEUTRAL"
            | "SKIPPED" -> CheckStatus.Skipped
            | "ACTION_REQUIRED"
            | "STALE" -> CheckStatus.Warn
            // Unknown / unmapped conclusion: surface as a warning rather
            // than silently claiming success.
            | _ -> CheckStatus.Warn

    /// PR `state` + `isDraft` + `reviewDecision` → our PrStatus. Total over
    /// the inputs; we query `states: [OPEN]` so MERGED/CLOSED are unusual
    /// here but stay handled for robustness.
    let mapPrStatus (state: string) (isDraft: bool) (reviewDecision: string) : PrStatus =
        match state with
        | "MERGED" -> PrStatus.Merged
        | "CLOSED" -> PrStatus.Closed
        | _ ->
            if isDraft then
                PrStatus.Draft
            else
                match reviewDecision with
                | "APPROVED" -> PrStatus.Approved
                | "CHANGES_REQUESTED"
                | "REVIEW_REQUIRED" -> PrStatus.InReview
                | _ -> PrStatus.Open

    /// Walk commits(last:1) → commit → checkSuites → checkRuns into a flat
    /// Check array. An empty array means "no checks reported yet", which
    /// the shell renders as a neutral state.
    let private toChecks (prNode: JsonElement) : Check array =
        let lastCommit =
            prNode
            |> prop "commits"
            |> ValueOption.map nodes
            |> ValueOption.bind (fun ns -> ns |> Seq.tryHead |> ValueOption.ofOption)
            |> ValueOption.bind (prop "commit")

        match lastCommit with
        | ValueNone -> Array.empty
        | ValueSome commit ->
            match prop "checkSuites" commit with
            | ValueNone -> Array.empty
            | ValueSome suites ->
                [| for suite in nodes suites do
                       match prop "checkRuns" suite with
                       | ValueSome runs ->
                           for run in nodes runs do
                               let name = str "name" run
                               let status = str "status" run
                               let conclusion = str "conclusion" run

                               { Name = name
                                 Status = mapCheckStatus status conclusion
                                 // Completed runs carry a conclusion; in-flight
                                 // ones only a status. Show whichever is live.
                                 Description = (if status <> "COMPLETED" then status else conclusion)
                                 Count = "" }
                       | ValueNone -> () |]

    let private toPullRequest (prNode: JsonElement) : PullRequest =
        let number = int' "number" prNode
        let state = str "state" prNode
        let isDraft = boolean "isDraft" prNode
        let reviewDecision = str "reviewDecision" prNode

        { Id = PullRequestId number
          Number = number
          Title = str "title" prNode
          Branch = toBranch (str "headRefName" prNode)
          Base = toBranch (str "baseRefName" prNode)
          Status = mapPrStatus state isDraft reviewDecision
          Checks = toChecks prNode
          // GitHub's MergeableState: MERGEABLE / CONFLICTING / UNKNOWN.
          MergeReady = (str "mergeable" prNode = "MERGEABLE") }

    /// Parse the GraphQL response envelope into our PullRequest list.
    /// A non-empty top-level `errors` array (GraphQL returns 200 + errors
    /// for partial/auth/cost failures) becomes a Failure carrying the first
    /// message. Malformed JSON likewise fails rather than throwing.
    let parse (responseJson: string) : OperationResult<IReadOnlyList<PullRequest>> =
        try
            use doc = JsonDocument.Parse responseJson
            let root = doc.RootElement

            match prop "errors" root with
            | ValueSome errors when errors.ValueKind = JsonValueKind.Array && errors.GetArrayLength() > 0 ->
                let first = errors.[0]
                let msg = str "message" first

                Failure(AveliaError.External("github-graphql", (if msg = "" then "GraphQL query failed" else msg)))
            | _ ->
                let prsConnection =
                    root
                    |> prop "data"
                    |> ValueOption.bind (prop "viewer")
                    |> ValueOption.bind (prop "pullRequests")

                let prs =
                    match prsConnection with
                    | ValueSome conn -> nodes conn |> Seq.map toPullRequest |> Seq.toArray
                    | ValueNone -> Array.empty

                Success(prs :> IReadOnlyList<_>)
        with :? JsonException as je ->
            Failure(AveliaError.External("github-graphql", $"malformed response: {je.Message}"))

// ----------------------------------------------------------------------------
//  Query text
// ----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module internal DashboardQueryText =

    /// Build the GraphQL request payload (`{"query": "..."}`) for the
    /// viewer's open PRs. `first` is inlined — this lib's
    /// <c>IConnection.Run</c> overload (0.4-beta) takes no variables bag,
    /// and the value is an int we control, never user input.
    let buildPayload (first: int) : string =
        let query =
            $"query {{ viewer {{ pullRequests(first: %d{first}, states: [OPEN], orderBy: {{field: UPDATED_AT, direction: DESC}}) {{ "
            + "nodes { number title isDraft mergeable state headRefName baseRefName reviewDecision "
            + "commits(last: 1) { nodes { commit { "
            + "statusCheckRollup { state } "
            + "checkSuites(first: 10) { nodes { checkRuns(first: 50) { nodes { name status conclusion } } } } "
            + "} } } } } } }"

        // Hand the GraphQL text through STJ so it's correctly escaped as a
        // JSON string value inside the request envelope.
        let sb = System.Text.Json.Nodes.JsonObject()
        sb.["query"] <- System.Text.Json.Nodes.JsonValue.Create query
        sb.ToJsonString()

// ----------------------------------------------------------------------------
//  Production impl over Octokit.GraphQL.IConnection
// ----------------------------------------------------------------------------

/// <see cref="IDashboardQuery"/> backed by Octokit.GraphQL's
/// <see cref="Octokit.GraphQL.IConnection"/>. Sends the hand-written query
/// payload and maps the response via <see cref="DashboardMapping"/>.
type OctokitGraphQlDashboardQuery(connection: IConnection, maxPullRequests: int) =

    /// Default page cap. GraphQL's `first` maxes at 100 per connection; 50
    /// open PRs authored by one user is already a heavy dashboard, and the
    /// query's nested check fan-out makes larger pages expensive in
    /// GraphQL points (separate 5000/h budget from REST).
    static member val DefaultMaxPullRequests = 50 with get

    new(connection: IConnection) =
        OctokitGraphQlDashboardQuery(connection, OctokitGraphQlDashboardQuery.DefaultMaxPullRequests)

    interface IDashboardQuery with
        member _.ListViewerPullRequestsAsync(ct: CancellationToken) =
            task {
                let payload = DashboardQueryText.buildPayload maxPullRequests

                // Mirror ApiClient.invoke's cancellation discipline: an
                // OperationCanceledException must propagate (not collapse
                // into a Failure), but reraise() is illegal inside a task
                // CE, so capture + rethrow via ExceptionDispatchInfo.
                let mutable cancelDispatch: ExceptionDispatchInfo | null = null

                let mutable result: OperationResult<IReadOnlyList<PullRequest>> =
                    Failure(AveliaError.Internal "unfilled")

                try
                    let! json = connection.Run(payload, ct)
                    result <- DashboardMapping.parse json
                with
                | :? OperationCanceledException as oce -> cancelDispatch <- ExceptionDispatchInfo.Capture oce
                | :? System.Net.Http.HttpRequestException as net -> result <- Failure(AveliaError.Network net.Message)
                | ex -> result <- Failure(AveliaError.External("github-graphql", ex.Message))

                match cancelDispatch with
                | null -> return result
                | dispatch ->
                    dispatch.Throw()
                    return result
            }

/// Null-object <see cref="IDashboardQuery"/> for clients constructed
/// without a GraphQL connection (the REST-only test path). Every call
/// fails fast with a clear source rather than NREs at the boundary.
[<RequireQualifiedAccess>]
module DashboardQuery =

    let unavailable: IDashboardQuery =
        { new IDashboardQuery with
            member _.ListViewerPullRequestsAsync(_ct: CancellationToken) =
                Task.FromResult(
                    Failure(AveliaError.External("github-graphql", "GraphQL connection not configured"))
                    : OperationResult<IReadOnlyList<PullRequest>>
                ) }
