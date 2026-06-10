namespace Avelia.Agent.Copilot

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open GitHub.Copilot
open Avelia.Core.Abstractions

/// Headless Copilot session: wraps one <c>CopilotClient</c> + its
/// <c>CopilotSession</c>, exposing the vendor-neutral
/// <c>IHeadlessAgentSession</c> contract.
///
/// The SDK pushes events through the <c>OnEvent</c> callback (wired at config
/// time by the factory) into <paramref name="channel"/>; <c>Events</c> drains
/// that channel as a single-consumer stream. Permission requests are bridged
/// through <paramref name="pending"/> — the SDK callback blocks on a
/// <c>TaskCompletionSource</c> that <c>RespondToPermissionAsync</c> resolves.
/// Cumulative cost lives in <paramref name="totals"/>, summed by the factory's
/// event sink and reported in the terminal <c>Ended</c> event.
///
/// One client per session (the SDK hosts its own CLI subprocess); disposal
/// tears down both.
type internal CopilotHeadlessSession
    (
        client: CopilotClient,
        session: CopilotSession,
        sessionId: SessionId,
        workspace: RepoPath,
        channel: Channel<AgentEvent>,
        pending: ConcurrentDictionary<Guid, TaskCompletionSource<Rpc.PermissionDecision>>,
        totals: CostSnapshot ref
    ) =

    let mutable consumed = 0
    let mutable interrupted = 0
    let mutable disposed = 0

    let exitTcs =
        TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously)

    // 130 = 128 + SIGINT, the conventional "terminated by Ctrl+C" exit code.
    // We use it for the forced-exit case so the shell's clean-vs-forced
    // rendering matches the terminal path's convention.
    let exitCode () = if interrupted = 0 then 0 else 130

    interface IAgentSession with
        member _.SessionId = sessionId
        member _.Workspace = workspace

        member _.InterruptAsync(ct) =
            Interlocked.Exchange(&interrupted, 1) |> ignore
            session.AbortAsync ct

        member _.WaitForExitAsync(ct) =
            task {
                use _reg = ct.Register(fun () -> exitTcs.TrySetCanceled ct |> ignore)
                return! exitTcs.Task
            }

    interface IHeadlessAgentSession with
        member _.Events(ct) =
            if Interlocked.Exchange(&consumed, 1) = 1 then
                invalidOp "Events is single-consumer; it has already been enumerated."

            taskSeq {
                for ev in channel.Reader.ReadAllAsync(ct) do
                    yield ev
            }

        member _.SendUserMessageAsync(text, refs, ct) =
            task {
                try
                    if isNull refs || refs.Length = 0 then
                        let! _ = session.SendAsync(text, ct)
                        // The send completes when the agent's turn is done; mark
                        // it on the same ordered channel so the pump sees it
                        // after the turn's content events.
                        channel.Writer.TryWrite AgentEvent.TurnEnded |> ignore
                        return Success()
                    else
                        let opts = MessageOptions(Prompt = text)
                        let attachments = ResizeArray<Attachment>()

                        for r in refs do
                            if not (String.IsNullOrWhiteSpace r) then
                                attachments.Add(AttachmentFile(Path = r, DisplayName = r) :> Attachment)

                        opts.Attachments <- attachments
                        let! _ = session.SendAsync(opts, ct)
                        channel.Writer.TryWrite AgentEvent.TurnEnded |> ignore
                        return Success()
                with ex ->
                    return Failure(AveliaError.External("copilot", ex.Message))
            }

        member _.RespondToPermissionAsync(requestId, decision, ct) =
            task {
                match pending.TryRemove requestId with
                | true, tcs ->
                    let sdk =
                        match decision with
                        | Allow
                        | AllowAlways -> Rpc.PermissionDecision.ApproveOnce()
                        | Deny -> Rpc.PermissionDecision.Reject("Denied by user.")

                    tcs.TrySetResult sdk |> ignore
                    return Success()
                | _ -> return Failure(AveliaError.NotFound(sprintf "permission:%O" requestId))
            }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            let work =
                task {
                    if Interlocked.Exchange(&disposed, 1) = 0 then
                        let clean = interrupted = 0
                        let code = exitCode ()

                        // Terminal Ended event, then complete the stream so a
                        // live Events consumer sees it and finishes.
                        channel.Writer.TryWrite(AgentEvent.Ended(code, totals.Value)) |> ignore
                        channel.Writer.TryComplete() |> ignore

                        // Release any in-flight permission callbacks so the SDK
                        // doesn't hang on a TCS that will never be answered.
                        for kv in pending do
                            kv.Value.TrySetResult(Rpc.PermissionDecision.Reject("Session disposed."))
                            |> ignore

                        pending.Clear()

                        exitTcs.TrySetResult { ExitCode = code; IsClean = clean } |> ignore

                        try
                            do! session.DisposeAsync()
                        with _ ->
                            ()

                        try
                            do! client.DisposeAsync()
                        with _ ->
                            ()
                }

            ValueTask(work)
