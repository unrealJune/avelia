namespace Avelia.Agent.Copilot

open System
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

/// Interactive Copilot session: the underlying <c>copilot</c> CLI hosted in a
/// ConPTY (supplied by an <c>ITerminalSessionFactory</c>), bypassing the SDK
/// entirely — the terminal IS the UI. The driver still owns the
/// <c>IAgentSession</c> lifecycle and forwards interrupt / wait / dispose to the
/// terminal. The chat event stream is empty in this mode by design.
type internal CopilotInteractiveSession(sessionId: SessionId, workspace: RepoPath, terminal: ITerminalSession) =

    interface IAgentSession with
        member _.SessionId = sessionId
        member _.Workspace = workspace
        member _.InterruptAsync(ct) = terminal.SendInterruptAsync ct
        member _.WaitForExitAsync(ct) = terminal.WaitForExitAsync ct

    interface IInteractiveAgentSession with
        member _.Terminal = terminal

    interface IAsyncDisposable with
        member _.DisposeAsync() = terminal.DisposeAsync()
