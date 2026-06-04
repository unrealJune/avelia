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

    let private mcpServer (m: McpServerConfig) : GitHub.Copilot.McpServerConfig =
        let stdio = McpStdioServerConfig(Command = m.Command)

        if not (isNull m.Args) && m.Args.Length > 0 then
            stdio.Args <- ResizeArray m.Args

        if not (isNull m.Env) && m.Env.Count > 0 then
            stdio.Env <- Dictionary m.Env

        stdio :> GitHub.Copilot.McpServerConfig

    /// Map our session config onto an SDK <c>SessionConfig</c>.
    ///
    /// <para>Mapped: working directory, model, allowed-tools filter, MCP
    /// servers, the resume session id, plus the event/permission callbacks.</para>
    ///
    /// <para>Not yet mapped (B-8 scope): <c>SystemPromptAppend</c> — the SDK's
    /// system-message override is a structured section/transform model that
    /// warrants its own wiring; the field is carried through the contract but
    /// ignored here until a later chunk needs it.</para>
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

        if not (isNull config.AllowedTools) && config.AllowedTools.Length > 0 then
            c.AvailableTools <- ResizeArray config.AllowedTools

        if not (isNull config.McpServers) && config.McpServers.Count > 0 then
            let dict = Dictionary<string, GitHub.Copilot.McpServerConfig>()

            for kv in config.McpServers do
                dict.[kv.Key] <- mcpServer kv.Value

            c.McpServers <- dict

        if not (String.IsNullOrEmpty config.ResumeSessionId) then
            c.SessionId <- config.ResumeSessionId

        c.OnEvent <- onEvent
        c.OnPermissionRequest <- onPermission
        c
