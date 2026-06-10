module Avelia.Agent.Copilot.Tests.CopilotConfigTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Xunit
open GitHub.Copilot
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

let private noEvent = Action<SessionEvent>(fun _ -> ())

let private noPermission =
    Func<GitHub.Copilot.PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>>(fun _ _ ->
        Task.FromResult(GitHub.Copilot.Rpc.PermissionDecision.ApproveOnce()))

let private baseConfig =
    { Workspace = RepoPath.Create "C:/work/repo"
      Model = Sonnet45
      ReasoningEffort = ReasoningEffort.High
      ContextTier = ContextTier.Default
      SystemPromptAppend = ""
      AllowedTools = [||]
      PermissionMode = PermissionMode.AcceptEdits
      McpServers = dict [] |> Dictionary :> IReadOnlyDictionary<_, _>
      ResumeSessionId = "" }

[<Fact>]
let ``maps workspace and model`` () =
    let c = CopilotConfig.build baseConfig noEvent noPermission
    Assert.Equal("C:/work/repo", c.WorkingDirectory)
    Assert.Equal("claude-sonnet-4.5", c.Model)

[<Fact>]
let ``maps reasoning effort onto the SDK wire token`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                ReasoningEffort = ReasoningEffort.High }
            noEvent
            noPermission

    Assert.Equal("high", c.ReasoningEffort)

[<Fact>]
let ``maps context tier onto the SDK long-context tier`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                ContextTier = ContextTier.LongContext }
            noEvent
            noPermission

    Assert.True(c.ContextTier.HasValue)
    Assert.Equal(GitHub.Copilot.ContextTier.LongContext, c.ContextTier.Value)

[<Fact>]
let ``maps default context tier`` () =
    let c = CopilotConfig.build baseConfig noEvent noPermission
    Assert.True(c.ContextTier.HasValue)
    Assert.Equal(GitHub.Copilot.ContextTier.Default, c.ContextTier.Value)

[<Fact>]
let ``blank mapped model leaves SDK Model unset`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                Model = CustomModel "" }
            noEvent
            noPermission

    Assert.True(String.IsNullOrEmpty c.Model)

[<Fact>]
let ``allowed tools become the available-tools filter`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                AllowedTools = [| "Edit"; "Read" |] }
            noEvent
            noPermission

    Assert.Equal<string list>([ "Edit"; "Read" ], List.ofSeq (nonNull c.AvailableTools))

[<Fact>]
let ``empty allowed tools leaves the filter unset (SDK default)`` () =
    let c = CopilotConfig.build baseConfig noEvent noPermission
    Assert.True(isNull c.AvailableTools)

[<Fact>]
let ``resume session id is carried through`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                ResumeSessionId = "sess-123" }
            noEvent
            noPermission

    Assert.Equal("sess-123", c.SessionId)

[<Fact>]
let ``mcp servers map to stdio config with command, args and env`` () =
    let mcp =
        McpServerConfig.Stdio(
            "node",
            [| "server.js"; "--port=3000" |],
            dict [ "TOKEN", "abc" ] |> Dictionary :> IReadOnlyDictionary<_, _>
        )

    let servers =
        dict [ "fs", mcp ] |> Dictionary :> IReadOnlyDictionary<string, Avelia.Core.Abstractions.McpServerConfig>

    let c =
        CopilotConfig.build { baseConfig with McpServers = servers } noEvent noPermission

    Assert.True((nonNull c.McpServers).ContainsKey "fs")

    match (nonNull c.McpServers).["fs"] with
    | :? McpStdioServerConfig as stdio ->
        Assert.Equal("node", stdio.Command)
        Assert.Equal<string list>([ "server.js"; "--port=3000" ], List.ofSeq (nonNull stdio.Args))
        Assert.Equal("abc", (nonNull stdio.Env).["TOKEN"])
        Assert.Equal<string list>([ "*" ], List.ofSeq (nonNull stdio.Tools))
    | other -> failwithf "expected stdio config, got %A" other

[<Fact>]
let ``mcp servers map http config with url and headers`` () =
    let mcp =
        McpServerConfig.Http(
            "http://127.0.0.1:5005/mcp/abc",
            dict [ "X-Avelia-Workspace", "abc" ] |> Dictionary :> IReadOnlyDictionary<_, _>
        )

    let servers =
        dict [ "avelia", mcp ] |> Dictionary :> IReadOnlyDictionary<string, Avelia.Core.Abstractions.McpServerConfig>

    let c =
        CopilotConfig.build { baseConfig with McpServers = servers } noEvent noPermission

    match (nonNull c.McpServers).["avelia"] with
    | :? McpHttpServerConfig as http ->
        Assert.Equal("http://127.0.0.1:5005/mcp/abc", http.Url)
        Assert.Equal("abc", (nonNull http.Headers).["X-Avelia-Workspace"])
        Assert.Equal<string list>([ "*" ], List.ofSeq (nonNull http.Tools))
    | other -> failwithf "expected http config, got %A" other

[<Fact>]
let ``empty system prompt append leaves the system message unset`` () =
    let c = CopilotConfig.build baseConfig noEvent noPermission
    Assert.Null(c.SystemMessage)

[<Fact>]
let ``non-empty system prompt append maps to an Append system message`` () =
    let c =
        CopilotConfig.build
            { baseConfig with
                SystemPromptAppend = "Call the avelia tools." }
            noEvent
            noPermission

    let sm = nonNull c.SystemMessage
    Assert.Equal(Nullable SystemMessageMode.Append, sm.Mode)
    Assert.Equal("Call the avelia tools.", sm.Content)

[<Fact>]
let ``callbacks are wired onto the config`` () =
    let c = CopilotConfig.build baseConfig noEvent noPermission
    Assert.NotNull(c.OnEvent)
    Assert.NotNull(c.OnPermissionRequest)
