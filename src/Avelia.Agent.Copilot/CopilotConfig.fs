namespace Avelia.Agent.Copilot

open System
open System.Collections.Generic
open System.Threading.Tasks
open GitHub.Copilot
open Avelia.Core.Abstractions

/// Builds the SDK's <c>SessionConfig</c> from our <c>AgentSessionConfig</c>.
/// Pure apart from the two delegates threaded in by the session wrapper (the
/// event sink and the permission bridge), so the field mapping is unit-testable
/// in isolation.
[<RequireQualifiedAccess>]
module CopilotConfig =

    // The CLI requires every MCP server to declare which of its tools to expose;
    // an omitted/empty list makes it reject the server ("No tools specified for
    // server"). We surface all of a server's tools, so request the wildcard.
    let private allTools () = ResizeArray [ "*" ]

    let private mcpServer (m: McpServerConfig) : GitHub.Copilot.McpServerConfig =
        match m with
        | McpServerConfig.Stdio(command, args, env) ->
            let stdio = McpStdioServerConfig(Command = command, Tools = allTools ())

            if args.Length > 0 then
                stdio.Args <- ResizeArray args

            if env.Count > 0 then
                stdio.Env <- Dictionary env

            stdio :> GitHub.Copilot.McpServerConfig

        | McpServerConfig.Http(url, headers) ->
            let http = McpHttpServerConfig(Url = url, Tools = allTools ())

            if headers.Count > 0 then
                http.Headers <- Dictionary headers

            http :> GitHub.Copilot.McpServerConfig

    /// Map our session config onto an SDK <c>SessionConfig</c>.
    ///
    /// <para>Mapped: working directory, model, reasoning effort, context tier,
    /// allowed-tools filter, MCP servers, an appended system message, the resume
    /// session id, plus the event/permission callbacks.</para>
    let build
        (config: AgentSessionConfig)
        (onEvent: Action<SessionEvent>)
        (onPermission:
            Func<GitHub.Copilot.PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>>)
        : SessionConfig =
        let c = SessionConfig()
        c.WorkingDirectory <- config.Workspace.Value

        let model = ModelMapping.toCopilotModelId config.Model

        if model <> "" then
            c.Model <- model

        // Reasoning + context are always set: the DUs carry a definite choice
        // (the user's settings default), mapped onto the SDK's wire vocabulary.
        c.ReasoningEffort <- config.ReasoningEffort.ApiValue

        let sdkTier =
            config.ContextTier.Match(
                (fun () -> GitHub.Copilot.ContextTier.Default),
                (fun () -> GitHub.Copilot.ContextTier.LongContext)
            )

        c.ContextTier <- Nullable sdkTier

        if config.AllowedTools.Length > 0 then
            c.AvailableTools <- ResizeArray config.AllowedTools

        if config.McpServers.Count > 0 then
            let dict = Dictionary<string, GitHub.Copilot.McpServerConfig>()

            for kv in config.McpServers do
                dict.[kv.Key] <- mcpServer kv.Value

            c.McpServers <- dict

        // Append (not replace) our guidance onto the agent's built-in system
        // prompt — Append mode keeps Copilot's default instructions intact.
        if not (String.IsNullOrWhiteSpace config.SystemPromptAppend) then
            c.SystemMessage <-
                SystemMessageConfig(Mode = Nullable SystemMessageMode.Append, Content = config.SystemPromptAppend)

        if not (String.IsNullOrEmpty config.ResumeSessionId) then
            c.SessionId <- config.ResumeSessionId

        c.OnEvent <- onEvent
        c.OnPermissionRequest <- onPermission
        c
