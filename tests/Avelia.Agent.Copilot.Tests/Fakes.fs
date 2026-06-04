namespace Avelia.Agent.Copilot.Tests

open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

/// A token source that returns a fixed result. Lets the factory's auth
/// short-circuit be exercised without a live credential store.
type FakeTokenSource(result: OperationResult<string>) =
    interface IGitHubTokenSource with
        member _.GetTokenAsync(_ct) = Task.FromResult result

/// Minimal in-memory <c>ITerminalSession</c>. Records the interrupt / dispose
/// calls so the interactive-session forwarding can be asserted, and lets a test
/// drive the exit result.
type FakeTerminalSession(?exitResult: ProcessExit) =
    let exit = defaultArg exitResult { ExitCode = 0; IsClean = true }
    member val Interrupted = 0 with get, set
    member val Disposed = 0 with get, set

    interface ITerminalSession with
        member _.Size = { Cols = 80; Rows = 24 }
        member _.WriteAsync(_bytes, _ct) = Task.CompletedTask
        member _.ReadAllAsync(_ct) = taskSeq { () }
        member _.ResizeAsync(_size, _ct) = Task.CompletedTask

        member this.SendInterruptAsync(_ct) =
            this.Interrupted <- this.Interrupted + 1
            Task.CompletedTask

        member _.WaitForExitAsync(_ct) = Task.FromResult exit

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Disposed <- this.Disposed + 1
            ValueTask.CompletedTask

/// A terminal factory that hands back <paramref name="session"/> (or a fixed
/// failure) and records the exact spawn arguments it was asked for.
type FakeTerminalSessionFactory(result: OperationResult<ITerminalSession>) =
    member val LastCommandLine = "" with get, set
    member val LastWorkingDirectory = "" with get, set
    member val LastSize = { Cols = 0; Rows = 0 } with get, set

    interface ITerminalSessionFactory with
        member this.StartAsync(commandLine, size, workingDirectory, _ct) =
            this.LastCommandLine <- commandLine
            this.LastWorkingDirectory <- workingDirectory
            this.LastSize <- size
            Task.FromResult result
