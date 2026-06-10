module Avelia.Agent.Copilot.Tests.HeadlessIntegrationTests

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

// ---------------------------------------------------------------------------
//  Live headless Copilot run. Gated on COPILOT_GITHUB_TOKEN (and a `copilot`
//  CLI on PATH, which the SDK spawns). When the token is absent the test
//  no-ops — it is Integration-tagged and excluded from the fast tier, and
//  exercises the real session only on a machine that is set up for it.
// ---------------------------------------------------------------------------

let private tokenFromEnv () =
    match Environment.GetEnvironmentVariable "COPILOT_GITHUB_TOKEN" with
    | null -> ""
    | t -> t

let private fixedTokenSource (token: string) =
    { new IGitHubTokenSource with
        member _.GetTokenAsync(_ct) = Task.FromResult(Success token) }

let private noTerminalFactory =
    { new ITerminalSessionFactory with
        member _.StartAsync(_cmd, _size, _wd, _ct) =
            Task.FromResult(Failure(AveliaError.Internal "interactive not exercised here")) }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``real headless run streams a conversation event`` () =
    task {
        let token = tokenFromEnv ()

        if String.IsNullOrWhiteSpace token then
            // Gated: no token configured on this machine. (Run with
            // COPILOT_GITHUB_TOKEN set, and the `copilot` CLI installed.)
            return ()
        else
            let work =
                Path.Combine(Path.GetTempPath(), "avelia-copilot-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory work |> ignore

            try
                let factory =
                    CopilotAgentSessionFactory(fixedTokenSource token, noTerminalFactory, CopilotSettings.defaults)
                    :> IAgentSessionFactory

                let config: AgentSessionConfig =
                    { Workspace = RepoPath.Create work
                      Model = Sonnet45
                      ReasoningEffort = ReasoningEffort.Medium
                      ContextTier = ContextTier.Default
                      SystemPromptAppend = ""
                      AllowedTools = [||]
                      PermissionMode = PermissionMode.AcceptEdits
                      McpServers = Dictionary<string, McpServerConfig>() :> IReadOnlyDictionary<_, _>
                      ResumeSessionId = "" }

                match! factory.StartHeadlessAsync(config, CancellationToken.None) with
                | Failure e -> return failwithf "StartHeadlessAsync failed: %A" e
                | Success session ->
                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 90.0)
                    let! _ = session.SendUserMessageAsync("Reply with the single word: hello.", [||], timeout.Token)

                    let mutable sawConversation = false
                    let e = session.Events(timeout.Token).GetAsyncEnumerator(timeout.Token)

                    try
                        let mutable go = true

                        while go && not sawConversation do
                            let! moved = e.MoveNextAsync()

                            if not moved then
                                go <- false
                            else
                                match e.Current with
                                | AgentEvent.Conversation _ -> sawConversation <- true
                                | AgentEvent.Ended _ -> go <- false
                                | _ -> ()
                    with :? OperationCanceledException ->
                        ()

                    do! e.DisposeAsync()
                    do! session.DisposeAsync()
                    Assert.True(sawConversation, "expected at least one conversation event from the agent")
            finally
                try
                    Directory.Delete(work, true)
                with _ ->
                    ()
    }
