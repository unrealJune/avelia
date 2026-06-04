module Avelia.Agent.Copilot.Tests.FactoryTests

open System.Collections.Generic
open System.Threading
open Xunit
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

let private config =
    { Workspace = RepoPath.Create "C:/work/repo"
      Model = Sonnet45
      SystemPromptAppend = ""
      AllowedTools = [||]
      PermissionMode = PermissionMode.AcceptEdits
      McpServers = Dictionary() :> IReadOnlyDictionary<_, _>
      ResumeSessionId = "" }

let private mkFactory (token: OperationResult<string>) (terminal: OperationResult<ITerminalSession>) =
    let tf = FakeTerminalSessionFactory terminal
    let factory = CopilotAgentSessionFactory(FakeTokenSource token, tf, CopilotSettings.defaults)
    factory :> IAgentSessionFactory, tf

// ---------------------------------------------------------------------------
//  Headless — auth short-circuits before any SDK / subprocess work
// ---------------------------------------------------------------------------

[<Fact>]
let ``headless propagates a token-source failure as-is`` () =
    let factory, _ = mkFactory (Failure(AveliaError.Network "offline")) (Failure AveliaError.Unauthorized)

    match (factory.StartHeadlessAsync(config, CancellationToken.None)).Result with
    | Failure(AveliaError.Network "offline") -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``headless treats an empty token as Unauthorized`` () =
    let factory, _ = mkFactory (Success "") (Failure AveliaError.Unauthorized)

    match (factory.StartHeadlessAsync(config, CancellationToken.None)).Result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> failwithf "unexpected %A" other

// ---------------------------------------------------------------------------
//  Interactive — never touches the SDK; just wraps the terminal factory
// ---------------------------------------------------------------------------

[<Fact>]
let ``interactive spawns the configured CLI in the workspace and wraps the terminal`` () =
    let terminal = FakeTerminalSession()
    let factory, tf = mkFactory (Success "tok") (Success(terminal :> ITerminalSession))

    let result = (factory.StartInteractiveAsync(config, CancellationToken.None)).Result

    let session =
        match result with
        | Success s -> s
        | Failure e -> failwithf "expected success, got %A" e

    Assert.Equal("copilot", tf.LastCommandLine)
    Assert.Equal("C:/work/repo", tf.LastWorkingDirectory)
    Assert.Equal({ Cols = 80; Rows = 24 }, tf.LastSize)
    Assert.Same(box terminal, box session.Terminal)
    Assert.Equal(config.Workspace, session.Workspace)

[<Fact>]
let ``interactive forwards interrupt, wait and dispose to the terminal`` () =
    let terminal = FakeTerminalSession({ ExitCode = 7; IsClean = false })
    let factory, _ = mkFactory (Success "tok") (Success(terminal :> ITerminalSession))
    let session = (factory.StartInteractiveAsync(config, CancellationToken.None)).Result.Value

    session.InterruptAsync(CancellationToken.None).Wait()
    Assert.Equal(1, terminal.Interrupted)

    let exit = session.WaitForExitAsync(CancellationToken.None).Result
    Assert.Equal(7, exit.ExitCode)
    Assert.False exit.IsClean

    (session.DisposeAsync()).AsTask().Wait()
    Assert.Equal(1, terminal.Disposed)

[<Fact>]
let ``interactive propagates a terminal-factory failure`` () =
    let factory, _ = mkFactory (Success "tok") (Failure(AveliaError.External("conpty", "no pty")))

    match (factory.StartInteractiveAsync(config, CancellationToken.None)).Result with
    | Failure(AveliaError.External("conpty", "no pty")) -> ()
    | other -> failwithf "unexpected %A" other
