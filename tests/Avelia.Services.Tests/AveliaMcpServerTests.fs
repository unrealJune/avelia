module Avelia.Services.Tests.AveliaMcpServerTests

open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Services

let private ct = CancellationToken.None

/// The loopback MCP URL for a workspace (reads it back out of the server's own
/// session-config map, exercising that path too).
let private urlFor (server: AveliaMcpServer) (ws: WorkspaceId) =
    match (server.McpServersFor ws).["avelia"] with
    | McpServerConfig.Http(url, _) -> url
    | other -> failwithf "expected an http config, got %A" other

let private post (url: string) (json: string) =
    task {
        use client = new HttpClient()
        use content = new StringContent(json, Encoding.UTF8, "application/json")
        let! resp = client.PostAsync(url, content)
        let! body = resp.Content.ReadAsStringAsync()
        return int resp.StatusCode, body
    }

let private samplePr (number: int) (title: string) : PullRequest =
    { Id = PullRequestId number
      Number = number
      Title = title
      Branch = BranchName.Create "speedbird"
      Base = BranchName.Create "main"
      Status = PrStatus.Open
      Checks = [||]
      MergeReady = false }

/// A server whose handlers just record their inputs and return scripted results.
let private mkServer (renameResult: OperationResult<unit>) (prResult: OperationResult<PullRequest>) =
    let renameCalls = ResizeArray<WorkspaceId * string>()
    let prCalls = ResizeArray<WorkspaceId * string * string * bool>()

    let rename ws title (_: CancellationToken) =
        renameCalls.Add(ws, title)
        Task.FromResult renameResult

    let createPr ws title body draft (_: CancellationToken) =
        prCalls.Add(ws, title, body, draft)
        Task.FromResult prResult

    let server = new AveliaMcpServer(rename, createPr)
    server, renameCalls, prCalls

[<Fact>]
let ``initialize returns protocol version and server info`` () =
    let server, _, _ = mkServer (Success()) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let status, body =
            (post (urlFor server ws) """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""").Result

        Assert.Equal(200, status)
        Assert.Contains("protocolVersion", body)
        Assert.Contains("avelia", body)
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``tools list advertises rename and create-pr tools`` () =
    let server, _, _ = mkServer (Success()) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let status, body =
            (post (urlFor server ws) """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""").Result

        Assert.Equal(200, status)
        Assert.Contains("rename_workspace", body)
        Assert.Contains("create_pull_request", body)
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``rename tool invokes the rename handler with the path workspace and title`` () =
    let server, renameCalls, _ = mkServer (Success()) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let req =
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"rename_workspace","arguments":{"title":"Add MCP Server"}}}"""

        let status, body = (post (urlFor server ws) req).Result

        Assert.Equal(200, status)
        Assert.Contains("\"isError\":false", body)
        Assert.Equal(1, renameCalls.Count)
        Assert.Equal(ws, fst renameCalls.[0])
        Assert.Equal("Add MCP Server", snd renameCalls.[0])
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``rename tool surfaces a handler failure as an error result`` () =
    let server, _, _ =
        mkServer (Failure(AveliaError.Validation "nope")) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let req =
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"rename_workspace","arguments":{"title":"Whatever"}}}"""

        let status, body = (post (urlFor server ws) req).Result

        Assert.Equal(200, status)
        Assert.Contains("\"isError\":true", body)
        Assert.Contains("nope", body)
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``create-pull-request tool invokes the handler and reports the number`` () =
    let server, _, prCalls = mkServer (Success()) (Success(samplePr 42 "My PR"))

    try
        let ws = WorkspaceId.create ()

        let req =
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"create_pull_request","arguments":{"title":"My PR","body":"desc","draft":true}}}"""

        let status, body = (post (urlFor server ws) req).Result

        Assert.Equal(200, status)
        Assert.Contains("\"isError\":false", body)
        Assert.Contains("#42", body)
        Assert.Equal(1, prCalls.Count)
        let (callWs, title, bodyArg, draft) = prCalls.[0]
        Assert.Equal(ws, callWs)
        Assert.Equal("My PR", title)
        Assert.Equal("desc", bodyArg)
        Assert.True(draft)
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``unknown tool returns an error result`` () =
    let server, _, _ = mkServer (Success()) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let req =
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"frobnicate","arguments":{}}}"""

        let status, body = (post (urlFor server ws) req).Result

        Assert.Equal(200, status)
        Assert.Contains("\"isError\":true", body)
        Assert.Contains("Unknown tool", body)
    finally
        (server :> System.IDisposable).Dispose()

[<Fact>]
let ``notifications get a 202 with no body`` () =
    let server, _, _ = mkServer (Success()) (Success(samplePr 1 "x"))

    try
        let ws = WorkspaceId.create ()

        let status, body =
            (post (urlFor server ws) """{"jsonrpc":"2.0","method":"notifications/initialized"}""").Result

        Assert.Equal(202, status)
        Assert.Equal("", body)
    finally
        (server :> System.IDisposable).Dispose()
