module Avelia.Core.Tests.BackendDomainTests

open System
open System.Collections.Generic
open Xunit
open Avelia.Core.Abstractions

// ----- AveliaError.External + updated Match -----

[<Fact>]
let ``AveliaError.External dispatches to onExternal`` () =
    let err = AveliaError.External("claude", "rate limited")

    let label =
        err.Match(
            onNotFound = Func<_, _>(fun _ -> "NotFound"),
            onValidation = Func<_, _>(fun _ -> "Validation"),
            onUnauthorized = Func<_>(fun () -> "Unauthorized"),
            onConflict = Func<_, _>(fun _ -> "Conflict"),
            onNetwork = Func<_, _>(fun _ -> "Network"),
            onInternal = Func<_, _>(fun _ -> "Internal"),
            onExternal = Func<_, _, _>(fun source detail -> $"External:{source}:{detail}")
        )

    Assert.Equal("External:claude:rate limited", label)

[<Fact>]
let ``AveliaError.Match still routes the original cases correctly`` () =
    let cases: (AveliaError * string) list =
        [ AveliaError.NotFound "r", "NotFound"
          AveliaError.Validation "v", "Validation"
          AveliaError.Unauthorized, "Unauthorized"
          AveliaError.Conflict "c", "Conflict"
          AveliaError.Network "n", "Network"
          AveliaError.Internal "i", "Internal" ]

    for err, expected in cases do
        let actual =
            err.Match(
                onNotFound = Func<_, _>(fun _ -> "NotFound"),
                onValidation = Func<_, _>(fun _ -> "Validation"),
                onUnauthorized = Func<_>(fun () -> "Unauthorized"),
                onConflict = Func<_, _>(fun _ -> "Conflict"),
                onNetwork = Func<_, _>(fun _ -> "Network"),
                onInternal = Func<_, _>(fun _ -> "Internal"),
                onExternal = Func<_, _, _>(fun _ _ -> "External")
            )

        Assert.Equal(expected, actual)

// ----- PermissionMode.Match -----

[<Fact>]
let ``PermissionMode.Match dispatches every case`` () =
    let cases: (PermissionMode * string) list =
        [ PermissionMode.AcceptEdits, "accept"
          PermissionMode.RequireApproval, "approval"
          PermissionMode.ReadOnly, "readonly"
          PermissionMode.Plan, "plan" ]

    for mode, expected in cases do
        let actual =
            mode.Match(
                acceptEdits = Func<_>(fun () -> "accept"),
                requireApproval = Func<_>(fun () -> "approval"),
                readOnly = Func<_>(fun () -> "readonly"),
                plan = Func<_>(fun () -> "plan")
            )

        Assert.Equal(expected, actual)

// ----- PermissionDecision.Match -----

[<Fact>]
let ``PermissionDecision.Match dispatches every case`` () =
    let cases: (PermissionDecision * string) list =
        [ Allow, "allow"; Deny, "deny"; AllowAlways, "always" ]

    for decision, expected in cases do
        let actual =
            decision.Match(
                allow = Func<_>(fun () -> "allow"),
                deny = Func<_>(fun () -> "deny"),
                allowAlways = Func<_>(fun () -> "always")
            )

        Assert.Equal(expected, actual)

// ----- AgentEvent.Match -----

[<Fact>]
let ``AgentEvent.Match routes each variant`` () =
    let snapshot =
        { InputTokens = 100
          OutputTokens = 50
          CostMicroUsd = 1_500L }

    let userMsg: UserMessage =
        { Id = MessageId.create ()
          Text = "hi"
          Refs = [||]
          Timestamp = DateTimeOffset.UnixEpoch }

    let convEvent = UserMessageAppended userMsg

    let permReq: PermissionRequest =
        { RequestId = Guid.NewGuid()
          ToolName = "Edit"
          ToolInputJson = "{}"
          Description = "edit foo.fs" }

    let cases: (AgentEvent * string) list =
        [ AgentEvent.Initialized("sess-1", ModelChoice.Sonnet45), "init"
          AgentEvent.Conversation convEvent, "conv"
          AgentEvent.CostUpdated snapshot, "cost"
          AgentEvent.PermissionRequired permReq, "perm"
          AgentEvent.RetryAttempt(1, 100, "rate"), "retry"
          AgentEvent.Warning "deprecated tool", "warn"
          AgentEvent.Ended(0, snapshot), "end" ]

    for event, expected in cases do
        let actual =
            event.Match(
                onInitialized = Func<_, _, _>(fun _ _ -> "init"),
                onConversation = Func<_, _>(fun _ -> "conv"),
                onCost = Func<_, _>(fun _ -> "cost"),
                onPermission = Func<_, _>(fun _ -> "perm"),
                onRetry = Func<_, _, _, _>(fun _ _ _ -> "retry"),
                onWarning = Func<_, _>(fun _ -> "warn"),
                onEnded = Func<_, _, _>(fun _ _ -> "end")
            )

        Assert.Equal(expected, actual)

[<Fact>]
let ``AgentEvent.Initialized carries the session id and model through Match`` () =
    let event = AgentEvent.Initialized("abc-123", ModelChoice.Opus41)

    let pair =
        event.Match(
            onInitialized = Func<_, _, _>(fun sid model -> sid, model),
            onConversation = Func<_, _>(fun _ -> "", ModelChoice.Sonnet45),
            onCost = Func<_, _>(fun _ -> "", ModelChoice.Sonnet45),
            onPermission = Func<_, _>(fun _ -> "", ModelChoice.Sonnet45),
            onRetry = Func<_, _, _, _>(fun _ _ _ -> "", ModelChoice.Sonnet45),
            onWarning = Func<_, _>(fun _ -> "", ModelChoice.Sonnet45),
            onEnded = Func<_, _, _>(fun _ _ -> "", ModelChoice.Sonnet45)
        )

    Assert.Equal(("abc-123", ModelChoice.Opus41), pair)
