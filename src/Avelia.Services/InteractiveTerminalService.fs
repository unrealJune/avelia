namespace Avelia.Services

open Avelia.Core.Abstractions

/// Real <c>ITerminalLaunchService</c>: resolves a workspace's worktree path +
/// model from the store and starts an interactive agent session there (the
/// agent factory hosts the CLI in a ConPTY). The shell binds
/// <c>session.Terminal</c> to a <c>TerminalView</c>.
type InteractiveTerminalService(workspaces: IWorkspaceStore, settings: ISettingsStore, factory: IAgentSessionFactory) =

    let emptyMcp =
        System.Collections.Generic.Dictionary<string, McpServerConfig>()
        :> System.Collections.Generic.IReadOnlyDictionary<_, _>

    interface ITerminalLaunchService with
        member _.StartAsync(workspaceId, ct) =
            task {
                match! workspaces.GetAsync(workspaceId, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    let! appearance = settings.LoadAsync ct

                    let config: AgentSessionConfig =
                        { Workspace = record.WorktreePath
                          Model = record.Workspace.Agent
                          ReasoningEffort = appearance.ReasoningEffort
                          ContextTier = appearance.ContextTier
                          SystemPromptAppend = ""
                          AllowedTools = [||]
                          PermissionMode = PermissionMode.AcceptEdits
                          McpServers = emptyMcp
                          ResumeSessionId = "" }

                    return! factory.StartInteractiveAsync(config, ct)
            }
