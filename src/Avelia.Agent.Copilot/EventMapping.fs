namespace Avelia.Agent.Copilot

open System
open GitHub.Copilot
open Avelia.Core.Abstractions

/// Translates the Copilot SDK's native <c>SessionEvent</c> hierarchy into the
/// vendor-neutral <c>AgentEvent</c> / <c>MessageEvent</c> shapes the chat
/// projection consumes. Keeping the mapping here (eager, at the boundary) is
/// the discipline backend.md calls for: the rest of the core never sees a
/// Copilot type, so a beta-SDK type churn only touches this file.
///
/// This module is deliberately **stateless** so it's unit-testable against
/// hand-constructed SDK event objects with no live runtime. The two stateful
/// signals — cumulative cost (needs a running total) and session lifecycle
/// (<c>Initialized</c> / <c>Ended</c>) — are owned by the session wrapper, not
/// here. <c>map</c> returns <c>[]</c> for everything it doesn't render, and a
/// list (not an option) because one assistant message can carry both prose and
/// tool requests.
[<RequireQualifiedAccess>]
module EventMapping =

    let private str (s: string | null) = if isNull s then "" else nonNull s

    /// Per-usage-event cost. The SDK reports USD as a nullable double; we
    /// convert to integral microdollars (1e-6 USD) so the boundary never
    /// carries float. The session wrapper sums these into a cumulative total.
    let usageDelta (d: AssistantUsageData) : CostSnapshot =
        let input = d.InputTokens |> Option.ofNullable |> Option.defaultValue 0L
        let output = d.OutputTokens |> Option.ofNullable |> Option.defaultValue 0L

        let micro =
            d.Cost
            |> Option.ofNullable
            |> Option.map (fun usd -> int64 (Math.Round(usd * 1_000_000.0)))
            |> Option.defaultValue 0L

        { InputTokens = int input
          OutputTokens = int output
          CostMicroUsd = micro }

    /// Extract a cost delta from an event, if it's a usage event. Separate from
    /// <c>map</c> because cost is accumulated statefully by the caller.
    let tryUsage (ev: SessionEvent) : CostSnapshot voption =
        match ev with
        | :? AssistantUsageEvent as e when not (isNull (box e.Data)) -> ValueSome(usageDelta e.Data)
        | _ -> ValueNone

    let private toolBatch (requests: AssistantMessageToolRequest[]) (ts: DateTimeOffset) : MessageEvent =
        let kinds =
            requests |> Array.map (fun r -> str r.Name) |> Array.filter (fun n -> n <> "")

        ToolBatchAppended
            { Id = MessageId.create ()
              ToolCount = requests.Length
              MessageCount = 0
              ToolKinds = kinds
              Timestamp = ts }

    /// Project an SDK event onto zero or more canonical <c>AgentEvent</c>s.
    /// Usage and lifecycle events are handled by the session wrapper and return
    /// <c>[]</c> here.
    let map (ev: SessionEvent) : AgentEvent list =
        let ts = ev.Timestamp

        match ev with
        | :? AssistantMessageEvent as e when not (isNull (box e.Data)) ->
            let d = e.Data

            let message =
                let content = str d.Content

                if content <> "" then
                    [ AgentEvent.Conversation(
                          AgentMessageAppended
                              { Id = MessageId.create ()
                                Text = content
                                Timestamp = ts }
                      ) ]
                else
                    []

            let tools =
                match d.ToolRequests with
                | null -> []
                | reqs when reqs.Length > 0 -> [ AgentEvent.Conversation(toolBatch reqs ts) ]
                | _ -> []

            message @ tools

        | :? SessionErrorEvent as e when not (isNull (box e.Data)) ->
            [ AgentEvent.Conversation(
                  AgentErrorAppended
                      { Id = MessageId.create ()
                        Text = str e.Data.Message
                        Timestamp = ts }
              ) ]

        | :? SessionWarningEvent as e when not (isNull (box e.Data)) -> [ AgentEvent.Warning(str e.Data.Message) ]

        | :? ModelCallFailureEvent as e when not (isNull (box e.Data)) ->
            [ AgentEvent.Warning("model call failed: " + str e.Data.ErrorMessage) ]

        | :? AbortEvent as e ->
            let reason = if isNull (box e.Data) then "" else str e.Data.Reason.Value

            [ AgentEvent.Warning(if reason = "" then "aborted" else "aborted: " + reason) ]

        | _ -> []
