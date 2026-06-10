namespace Avelia.Agent.Copilot

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Threading.Channels
open GitHub.Copilot
open Avelia.Core.Abstractions

/// Bridges the SDK's synchronous-return permission callback to the host's
/// asynchronous approve/deny flow, per <c>PermissionMode</c>:
///
/// <list type="bullet">
/// <item><c>AcceptEdits</c> — approve every gated call inline; no host round-trip.</item>
/// <item><c>ReadOnly</c> / <c>Plan</c> — reject every gated call (mutations are
/// what trigger a permission prompt; rejecting them yields a read-only run).</item>
/// <item><c>RequireApproval</c> — register a pending completion keyed by a fresh
/// id, emit <c>AgentEvent.PermissionRequired</c>, and block the SDK callback on
/// that completion until <c>RespondToPermissionAsync</c> resolves it.</item>
/// </list>
[<RequireQualifiedAccess>]
module CopilotPermissions =

    let handle
        (mode: PermissionMode)
        (pending: ConcurrentDictionary<Guid, TaskCompletionSource<Rpc.PermissionDecision>>)
        (channel: Channel<AgentEvent>)
        (req: GitHub.Copilot.PermissionRequest)
        : Task<Rpc.PermissionDecision> =
        match mode with
        | PermissionMode.AcceptEdits -> Task.FromResult(Rpc.PermissionDecision.ApproveOnce())

        | PermissionMode.ReadOnly
        | PermissionMode.Plan -> Task.FromResult(Rpc.PermissionDecision.Reject("Read-only session: mutation rejected."))

        | PermissionMode.RequireApproval ->
            let id = Guid.NewGuid()

            let tcs =
                TaskCompletionSource<Rpc.PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously)

            pending.[id] <- tcs

            let request: Avelia.Core.Abstractions.PermissionRequest =
                { RequestId = id
                  ToolName = (if isNull (box req.Kind) then "" else req.Kind)
                  ToolInputJson = ""
                  Description = "" }

            channel.Writer.TryWrite(AgentEvent.PermissionRequired request) |> ignore
            tcs.Task
