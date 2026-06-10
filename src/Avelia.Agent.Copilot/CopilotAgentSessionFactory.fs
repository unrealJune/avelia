namespace Avelia.Agent.Copilot

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open GitHub.Copilot
open Avelia.Core.Abstractions

/// Factory for Copilot agent sessions. One <c>CopilotClient</c> (and its hosted
/// CLI subprocess) is created per headless session; interactive sessions host
/// the raw <c>copilot</c> CLI in a ConPTY via the injected terminal factory.
///
/// Auth reuses B-3: <paramref name="tokenSource"/> yields the user's stored
/// GitHub token. A token failure surfaces as <c>Unauthorized</c> rather than a
/// crash. The shell registers one instance of this in Composition (B-12).
type CopilotAgentSessionFactory
    (tokenSource: IGitHubTokenSource, terminalFactory: ITerminalSessionFactory, settings: CopilotSettings) =

    let buildClient (token: string) (workspace: RepoPath) =
        let opts =
            CopilotClientOptions(
                Mode = CopilotClientMode.CopilotCli,
                GitHubToken = token,
                WorkingDirectory = workspace.Value
            )

        new CopilotClient(opts)

    interface IAgentSessionFactory with
        member _.StartHeadlessAsync(config, ct) =
            task {
                let! tokenResult = tokenSource.GetTokenAsync ct

                match tokenResult with
                | Failure e -> return Failure e
                | Success token when String.IsNullOrEmpty token -> return Failure AveliaError.Unauthorized
                | Success token ->
                    let mutable client = Unchecked.defaultof<CopilotClient>

                    try
                        client <- buildClient token config.Workspace
                        do! client.StartAsync ct

                        let channel = Channel.CreateUnbounded<AgentEvent>()

                        let pending =
                            ConcurrentDictionary<Guid, TaskCompletionSource<Rpc.PermissionDecision>>()

                        let totals =
                            ref
                                { InputTokens = 0
                                  OutputTokens = 0
                                  CostMicroUsd = 0L }

                        let totalsLock = obj ()

                        let onEvent (ev: SessionEvent) =
                            match EventMapping.tryUsage ev with
                            | ValueSome delta ->
                                lock totalsLock (fun () ->
                                    let t = totals.Value

                                    totals.Value <-
                                        { InputTokens = t.InputTokens + delta.InputTokens
                                          OutputTokens = t.OutputTokens + delta.OutputTokens
                                          CostMicroUsd = t.CostMicroUsd + delta.CostMicroUsd }

                                    channel.Writer.TryWrite(AgentEvent.CostUpdated totals.Value) |> ignore)
                            | ValueNone -> ()

                            for ae in EventMapping.map ev do
                                channel.Writer.TryWrite ae |> ignore

                        let onPermission
                            (req: GitHub.Copilot.PermissionRequest)
                            (_inv: PermissionInvocation)
                            : Task<Rpc.PermissionDecision> =
                            CopilotPermissions.handle config.PermissionMode pending channel req

                        let sdkConfig =
                            CopilotConfig.build config (Action<SessionEvent> onEvent) (Func<_, _, _> onPermission)

                        let! session = client.CreateSessionAsync(sdkConfig, ct)

                        // Synthesize the contract's mandatory first event; the
                        // SDK's own session id becomes our ResumeSessionId later.
                        channel.Writer.TryWrite(AgentEvent.Initialized(session.SessionId, config.Model))
                        |> ignore

                        let headless =
                            CopilotHeadlessSession(
                                client,
                                session,
                                SessionId.create (),
                                config.Workspace,
                                channel,
                                pending,
                                totals
                            )

                        return Success(headless :> IHeadlessAgentSession)
                    with ex ->
                        if not (obj.ReferenceEquals(client, null)) then
                            try
                                do! client.DisposeAsync()
                            with _ ->
                                ()

                        return Failure(AveliaError.External("copilot", ex.Message))
            }

        member _.StartInteractiveAsync(config, ct) =
            task {
                let size = { Cols = 80; Rows = 24 }
                let! terminalResult = terminalFactory.StartAsync(settings.CliCommand, size, config.Workspace.Value, ct)

                match terminalResult with
                | Failure e -> return Failure e
                | Success terminal ->
                    let session =
                        CopilotInteractiveSession(SessionId.create (), config.Workspace, terminal)

                    return Success(session :> IInteractiveAgentSession)
            }
