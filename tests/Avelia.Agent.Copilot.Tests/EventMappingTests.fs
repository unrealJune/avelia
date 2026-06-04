module Avelia.Agent.Copilot.Tests.EventMappingTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open GitHub.Copilot
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

// SDK DTOs use C# `required` members, so these builders set every required
// property (the ones our mapping ignores get harmless defaults) to keep the
// tests focused on the fields that actually drive the projection.

let private usage (input: int64) (output: int64) (cost: float option) =
    AssistantUsageData(
        Model = "test-model",
        InputTokens = Nullable input,
        OutputTokens = Nullable output,
        Cost = (match cost with
                | Some c -> Nullable c
                | None -> Nullable())
    )

let private toolReq (name: string) =
    AssistantMessageToolRequest(Name = name, ToolCallId = "call-" + name)

let private msgData (content: string) (tools: AssistantMessageToolRequest[]) =
    AssistantMessageData(Content = content, MessageId = "m1", ToolRequests = tools)

// ---------------------------------------------------------------------------
//  usageDelta — USD→microUSD, nullable handling
// ---------------------------------------------------------------------------

[<Fact>]
let ``usageDelta converts tokens and USD cost to microdollars`` () =
    let snap = EventMapping.usageDelta (usage 120L 45L (Some 0.002))
    Assert.Equal(120, snap.InputTokens)
    Assert.Equal(45, snap.OutputTokens)
    Assert.Equal(2000L, snap.CostMicroUsd) // 0.002 USD * 1e6

[<Fact>]
let ``usageDelta treats nulls as zero`` () =
    let snap = EventMapping.usageDelta (AssistantUsageData(Model = "m"))
    Assert.Equal(0, snap.InputTokens)
    Assert.Equal(0, snap.OutputTokens)
    Assert.Equal(0L, snap.CostMicroUsd)

[<Property>]
let ``usageDelta never produces negative cost for non-negative USD`` (cents: NonNegativeInt) =
    let usd = float cents.Get / 100.0
    let snap = EventMapping.usageDelta (usage 0L 0L (Some usd))
    snap.CostMicroUsd >= 0L

[<Fact>]
let ``tryUsage returns None for a non-usage event`` () =
    Assert.True((EventMapping.tryUsage (SessionIdleEvent(Data = SessionIdleData())) |> ValueOption.isNone))

[<Fact>]
let ``tryUsage returns Some for a usage event`` () =
    let ev = AssistantUsageEvent(Data = usage 3L 0L None)
    Assert.True((EventMapping.tryUsage ev |> ValueOption.isSome))

// ---------------------------------------------------------------------------
//  map — conversation / diagnostics projection
// ---------------------------------------------------------------------------

let private single (events: AgentEvent list) =
    Assert.Equal(1, List.length events)
    List.head events

[<Fact>]
let ``assistant message with content maps to one AgentMessageAppended`` () =
    let ev = AssistantMessageEvent(Data = msgData "hello world" [||])

    match single (EventMapping.map ev) with
    | AgentEvent.Conversation(AgentMessageAppended m) -> Assert.Equal("hello world", m.Text)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``assistant message with tool requests maps to a ToolBatch with names`` () =
    let data = msgData "" [| toolReq "Edit"; toolReq "Shell" |]

    match single (EventMapping.map (AssistantMessageEvent(Data = data))) with
    | AgentEvent.Conversation(ToolBatchAppended b) ->
        Assert.Equal(2, b.ToolCount)
        Assert.Equal<string[]>([| "Edit"; "Shell" |], b.ToolKinds)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``assistant message with both prose and tools emits message then tool batch`` () =
    let data = msgData "doing it" [| toolReq "Edit" |]

    match EventMapping.map (AssistantMessageEvent(Data = data)) with
    | [ AgentEvent.Conversation(AgentMessageAppended _); AgentEvent.Conversation(ToolBatchAppended _) ] -> ()
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``empty assistant content with no tools maps to nothing`` () =
    Assert.Empty(EventMapping.map (AssistantMessageEvent(Data = msgData "" [||])))

[<Fact>]
let ``session warning maps to AgentEvent Warning`` () =
    let ev = SessionWarningEvent(Data = SessionWarningData(Message = "low on quota", WarningType = "quota"))

    match single (EventMapping.map ev) with
    | AgentEvent.Warning msg -> Assert.Equal("low on quota", msg)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``session error maps to AgentErrorAppended`` () =
    let ev = SessionErrorEvent(Data = SessionErrorData(Message = "boom", ErrorType = "fatal"))

    match single (EventMapping.map ev) with
    | AgentEvent.Conversation(AgentErrorAppended e) -> Assert.Equal("boom", e.Text)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``abort maps to a warning carrying the reason`` () =
    let ev = AbortEvent(Data = AbortData(Reason = AbortReason "user_initiated"))

    match single (EventMapping.map ev) with
    | AgentEvent.Warning msg -> Assert.Equal("aborted: user_initiated", msg)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``unhandled event kinds map to nothing`` () =
    Assert.Empty(EventMapping.map (SessionIdleEvent(Data = SessionIdleData())))

[<Property>]
let ``any non-empty content round-trips into the message text`` (content: NonEmptyString) =
    let text = content.Get

    match EventMapping.map (AssistantMessageEvent(Data = msgData text [||])) with
    | [ AgentEvent.Conversation(AgentMessageAppended m) ] -> m.Text = text
    | _ -> false
