namespace Avelia.Services

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open Avelia.Core.Abstractions

/// Per-conversation orchestration state: the broadcast subscriber set, the lazy
/// headless session, its event-pump task, and the synchronization primitives
/// that keep session start/stop single-flighted.
type private ConvState() =
    let mutable renameClaimed = 0
    /// Live observers; mutated under <c>SubGate</c>.
    member val Subscribers = ResizeArray<Channel<ConversationUpdate>>()
    member val SubGate = obj ()
    /// Serializes session start + message send so two near-simultaneous posts
    /// can't double-start, and sends stay ordered.
    member val StartGate = new SemaphoreSlim(1, 1)
    member val Session: IHeadlessAgentSession option = None with get, set
    member val Pump: Task = Task.CompletedTask with get, set
    member val Cts: CancellationTokenSource option = None with get, set

    /// One-shot claim for the Haiku auto-rename. Returns <c>true</c> exactly
    /// once per conversation (per process); every later call returns
    /// <c>false</c> so the rename fires at most once.
    member _.TryClaimRename() =
        System.Threading.Interlocked.Exchange(&renameClaimed, 1) = 0

/// Real <c>IConversationService</c>: drives a per-workspace headless Copilot
/// session and projects its <c>AgentEvent</c> stream into the conversation's
/// event-sourced message stream.
///
/// Lifecycle: a session is started lazily on the first user message (so opening
/// a workspace doesn't spawn a CLI), and torn down on archive
/// (<c>DisposeConversationAsync</c>) or service disposal. The event pump is the
/// sole consumer of the single-consumer <c>session.Events</c> stream; it appends
/// each conversation event to the store and broadcasts it to observers.
type AgentConversationService
    (
        factory: IAgentSessionFactory,
        conversations: IConversationStore,
        workspaces: IWorkspaceStore,
        settings: ISettingsStore,
        now: unit -> DateTimeOffset
    ) =

    let states = ConcurrentDictionary<ConversationId, ConvState>()
    let lifetime = new CancellationTokenSource()
    let emptyMcp = Dictionary<string, McpServerConfig>() :> IReadOnlyDictionary<_, _>

    let stateFor (convId: ConversationId) =
        states.GetOrAdd(convId, fun _ -> ConvState())

    let broadcast (state: ConvState) (update: ConversationUpdate) =
        let snapshot = lock state.SubGate (fun () -> state.Subscribers.ToArray())

        for ch in snapshot do
            ch.Writer.TryWrite update |> ignore

    let describe (e: AveliaError) =
        match e with
        | AveliaError.Unauthorized -> "Not authorized — sign in to GitHub or set a token in Settings → Agents."
        | AveliaError.Network m -> "Network error: " + m
        | AveliaError.External(src, d) -> sprintf "%s error: %s" src d
        | AveliaError.NotFound r -> "Not found: " + r
        | AveliaError.Validation m -> m
        | AveliaError.Conflict m -> m
        | AveliaError.Internal m -> "Internal error: " + m

    /// Persist an error message and surface it in the chat stream.
    let pushError (convId: ConversationId) (state: ConvState) (text: string) =
        task {
            let ev =
                AgentErrorAppended
                    { Id = MessageId.create ()
                      Text = text
                      Timestamp = now () }

            let! _ = conversations.AppendEventAsync(convId, ev, lifetime.Token)
            broadcast state (MessageAppended ev)
        }

    /// Clean a model's raw reply into a short display title: first non-empty
    /// line, stripped of wrapping quotes / surrounding whitespace / a trailing
    /// period, clamped to ~60 chars. Empty when nothing usable remains.
    let sanitizeTitle (raw: string) : string =
        let s = if String.IsNullOrEmpty raw then "" else raw

        let firstLine =
            s.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue ""

        let trimmed = firstLine.Trim([| '"'; '\''; '\u201C'; '\u201D'; ' '; '.'; '\t' |])

        if trimmed.Length > 60 then
            trimmed.Substring(0, 60).Trim()
        else
            trimmed

    /// Best-effort one-shot rename: open a headless Haiku session against the
    /// worktree (read-only), ask for a 3–6 word Title-Case summary of the
    /// task's first user message, take the first assistant reply, and persist
    /// it as a <c>TitleChanged</c> event. Failures (no token, timeout, blank
    /// reply) leave the original title untouched. Runs off the pump.
    let renameConversation
        (convId: ConversationId)
        (state: ConvState)
        (workspaceId: WorkspaceId)
        (firstUserText: string)
        : Task<unit> =
        task {
            match! workspaces.GetAsync(workspaceId, lifetime.Token) with
            | Failure _ -> ()
            | Success record ->
                let prompt =
                    "Summarize this coding task as a 3-6 word title in Title Case. "
                    + "No punctuation, no quotes, no trailing period. Respond with only the title. "
                    + "Task: "
                    + firstUserText

                let config: AgentSessionConfig =
                    { Workspace = record.WorktreePath
                      Model = Haiku45
                      ReasoningEffort = ReasoningEffort.Medium
                      ContextTier = ContextTier.Default
                      SystemPromptAppend = ""
                      AllowedTools = [||]
                      PermissionMode = PermissionMode.ReadOnly
                      McpServers = emptyMcp
                      ResumeSessionId = "" }

                match! factory.StartHeadlessAsync(config, lifetime.Token) with
                | Failure _ -> ()
                | Success session ->
                    let mutable title = ""

                    try
                        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds 30.0)

                        match! session.SendUserMessageAsync(prompt, [||], timeoutCts.Token) with
                        | Failure _ -> ()
                        | Success() ->
                            for ev in session.Events timeoutCts.Token do
                                match ev with
                                | AgentEvent.Conversation(AgentMessageAppended a) when title = "" ->
                                    title <- sanitizeTitle a.Text
                                    // Got the title — stop draining the stream.
                                    timeoutCts.Cancel()
                                | _ -> ()
                    with _ ->
                        ()

                    do! session.DisposeAsync()

                    if not (String.IsNullOrWhiteSpace title) then
                        let! _ = conversations.AppendEventAsync(convId, TitleChanged title, lifetime.Token)
                        broadcast state (MessageAppended(TitleChanged title))
        }

    /// Fire the Haiku rename if <paramref name="conv"/> is the conversation's
    /// very first assistant reply (so a restart's later replies don't re-rename)
    /// and a first user message exists. One-shot via <c>TryClaimRename</c>.
    let maybeStartRename (convId: ConversationId) (state: ConvState) (conv: Conversation) =
        let assistantCount =
            conv.Messages
            |> Array.sumBy (function
                | AgentMessageAppended _ -> 1
                | _ -> 0)

        let firstUser =
            conv.Messages
            |> Array.tryPick (function
                | UserMessageAppended u -> Some u.Text
                | _ -> None)

        match firstUser with
        | Some userText when assistantCount = 1 && not (String.IsNullOrWhiteSpace userText) ->
            Task.Run(fun () -> renameConversation convId state conv.WorkspaceId userText :> Task)
            |> ignore
        | _ -> ()

    /// Pump one session's events into the store + observers. The only consumer
    /// of <c>session.Events</c>. On exit (normal or fault) it clears
    /// <c>state.Session</c> so the next post restarts cleanly.
    let runPump
        (convId: ConversationId)
        (state: ConvState)
        (session: IHeadlessAgentSession)
        (token: CancellationToken)
        =
        task {
            try
                for ev in session.Events token do
                    match ev with
                    | AgentEvent.Conversation msgEvent ->
                        let! appended = conversations.AppendEventAsync(convId, msgEvent, token)
                        broadcast state (MessageAppended msgEvent)

                        // First assistant reply of a brand-new conversation:
                        // kick off the one-shot Haiku display-title rename.
                        match msgEvent, appended with
                        | AgentMessageAppended _, Success conv when state.TryClaimRename() ->
                            maybeStartRename convId state conv
                        | _ -> ()
                    | AgentEvent.TurnEnded -> broadcast state TurnCompleted
                    | AgentEvent.Warning w -> do! pushError convId state w
                    | AgentEvent.Ended(code, _) when code <> 0 ->
                        do! pushError convId state (sprintf "Agent exited with code %d." code)
                    | _ -> () // Initialized / CostUpdated / RetryAttempt / Ended(0) / PermissionRequired
            with ex ->
                do! pushError convId state ("Agent stream ended unexpectedly: " + ex.Message)

            // Reset so a subsequent post restarts the session. Guarded by the
            // same gate that start/send use, so it can't race an in-flight send.
            do! state.StartGate.WaitAsync()

            try
                state.Session <- None
            finally
                state.StartGate.Release() |> ignore
        }
        :> Task

    /// Build the session config for a conversation's workspace.
    let startSession (convId: ConversationId) (workspaceId: WorkspaceId) =
        task {
            match! workspaces.GetAsync(workspaceId, lifetime.Token) with
            | Failure e -> return Error e
            | Success record ->
                let! appearance = settings.LoadAsync lifetime.Token

                let config: AgentSessionConfig =
                    { Workspace = record.WorktreePath
                      Model = record.Workspace.Agent
                      ReasoningEffort = appearance.ReasoningEffort
                      ContextTier = appearance.ContextTier
                      SystemPromptAppend = ""
                      AllowedTools = [||]
                      PermissionMode = PermissionMode.AcceptEdits
                      McpServers = emptyMcp
                      ResumeSessionId = "" }

                match! factory.StartHeadlessAsync(config, lifetime.Token) with
                | Success session -> return Ok session
                | Failure e -> return Error e
        }

    // ---- IConversationService -------------------------------------------

    interface IConversationService with
        member _.GetForWorkspaceAsync(workspaceId, ct) =
            conversations.GetByWorkspaceAsync(workspaceId, ct)

        member _.PostUserMessageAsync(conversationId, text, refs, ct) =
            task {
                match! conversations.GetAsync(conversationId, ct) with
                | Failure e -> return Failure e
                | Success conv ->
                    let userMsg =
                        { Id = MessageId.create ()
                          Text = text
                          Refs = refs
                          Timestamp = now () }

                    let! _ = conversations.AppendEventAsync(conversationId, UserMessageAppended userMsg, ct)
                    let state = stateFor conversationId
                    broadcast state (MessageAppended(UserMessageAppended userMsg))

                    // Drive the agent on a worker with the service lifetime token
                    // (not the post's ct, which dies with the UI action), so the
                    // post returns promptly.
                    let workspaceId = conv.WorkspaceId

                    Task.Run(fun () ->
                        task {
                            do! state.StartGate.WaitAsync lifetime.Token

                            try
                                if state.Session.IsNone then
                                    match! startSession conversationId workspaceId with
                                    | Ok session ->
                                        state.Session <- Some session
                                        let cts = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                                        state.Cts <- Some cts
                                        state.Pump <- runPump conversationId state session cts.Token
                                    | Error e -> do! pushError conversationId state (describe e)

                                match state.Session with
                                | Some session ->
                                    match! session.SendUserMessageAsync(text, refs, lifetime.Token) with
                                    | Success() -> ()
                                    | Failure e -> do! pushError conversationId state (describe e)
                                | None -> ()
                            finally
                                state.StartGate.Release() |> ignore
                        }
                        :> Task)
                    |> ignore

                    return Success userMsg
            }

        member _.ObserveMessages(conversationId, ct) =
            let state = stateFor conversationId

            let channel =
                Channel.CreateUnbounded<ConversationUpdate>(
                    UnboundedChannelOptions(SingleReader = true, AllowSynchronousContinuations = false)
                )

            lock state.SubGate (fun () -> state.Subscribers.Add channel)

            let cleanup () =
                channel.Writer.TryComplete() |> ignore
                lock state.SubGate (fun () -> state.Subscribers.Remove channel |> ignore)

            let registration = ct.Register(Action cleanup)

            channel.Reader.Completion.ContinueWith(
                (fun _ -> registration.Dispose()),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore

            channel.Reader.ReadAllAsync ct

    /// Tear down the agent session for a conversation (archive flow). Idempotent.
    member _.DisposeConversationAsync(conversationId: ConversationId) : Task<unit> =
        task {
            match states.TryGetValue conversationId with
            | true, state ->
                state.Cts |> Option.iter (fun c -> c.Cancel())

                match state.Session with
                | Some session ->
                    try
                        do! session.DisposeAsync()
                    with _ ->
                        ()
                | None -> ()

                state.Session <- None

                let channels = lock state.SubGate (fun () -> state.Subscribers.ToArray())

                for ch in channels do
                    ch.Writer.TryComplete() |> ignore
            | _ -> ()
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            let work =
                task {
                    lifetime.Cancel()

                    for kv in states do
                        do! this.DisposeConversationAsync kv.Key
                }

            ValueTask(work)
