module Avelia.Agent.Copilot.Tests.CopilotPermissionsTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Threading.Channels
open Xunit
open GitHub.Copilot
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

let private fresh () =
    Channel.CreateUnbounded<AgentEvent>(),
    ConcurrentDictionary<Guid, TaskCompletionSource<Rpc.PermissionDecision>>()

let private approveKind = Rpc.PermissionDecision.ApproveOnce().Kind
let private rejectKind = (Rpc.PermissionDecision.Reject "x").Kind

[<Fact>]
let ``AcceptEdits approves inline with no event emitted`` () =
    let channel, pending = fresh ()
    let req = GitHub.Copilot.PermissionRequest(Kind = "write")
    let decision = (CopilotPermissions.handle PermissionMode.AcceptEdits pending channel req).Result
    Assert.Equal(approveKind, decision.Kind)
    Assert.False(fst (channel.Reader.TryRead())) // nothing queued
    Assert.Empty pending

[<Theory>]
[<InlineData "readonly">]
[<InlineData "plan">]
let ``ReadOnly and Plan reject inline`` (mode: string) =
    let channel, pending = fresh ()
    let m = if mode = "plan" then PermissionMode.Plan else PermissionMode.ReadOnly
    let req = GitHub.Copilot.PermissionRequest(Kind = "write")
    let decision = (CopilotPermissions.handle m pending channel req).Result
    Assert.Equal(rejectKind, decision.Kind)
    Assert.Empty pending

[<Fact>]
let ``RequireApproval emits a PermissionRequired event and blocks on the host`` () =
    let channel, pending = fresh ()
    let req = GitHub.Copilot.PermissionRequest(Kind = "write")
    let task = CopilotPermissions.handle PermissionMode.RequireApproval pending channel req

    // The SDK callback is pending until the host answers.
    Assert.False task.IsCompleted

    let ok, ev = channel.Reader.TryRead()
    Assert.True ok

    let requestId =
        match ev with
        | AgentEvent.PermissionRequired r ->
            Assert.Equal("write", r.ToolName)
            r.RequestId
        | other -> failwithf "unexpected %A" other

    // A pending completion was registered under that id; resolving it unblocks.
    Assert.True(pending.ContainsKey requestId)
    pending.[requestId].TrySetResult(Rpc.PermissionDecision.ApproveOnce()) |> ignore
    Assert.Equal(approveKind, task.Result.Kind)
