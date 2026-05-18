namespace Avelia.Core

open System
open Avelia.Core.Abstractions

// ============================================================================
//  Task (legacy — preserved for the existing test suite while the design's
//  Workspace-centric model takes over downstream).
// ============================================================================

type TaskStatus =
    | Drafting
    | Active
    | Blocked of reason: string
    | InReview of prId: PullRequestId
    | Merged of mergedAt: DateTimeOffset
    | Archived
    | Abandoned of reason: string

type Task =
    { Id: TaskId
      Title: string
      Status: TaskStatus
      CreatedAt: DateTimeOffset }

module Task =
    /// Allowed state transitions for a task. Total over the input domain —
    /// every (from, to) pair has a defined answer.
    let canTransition (from: TaskStatus) (to': TaskStatus) =
        match from, to' with
        | Drafting, Active -> true
        | Active, (Blocked _ | InReview _ | Abandoned _) -> true
        | InReview _, (Merged _ | Active) -> true
        | Merged _, Archived -> true
        | _ -> false

// ============================================================================
//  Workspace rules
// ============================================================================

module Workspace =
    /// Allowed workspace status transitions. Mirrors the user-visible flow
    /// from the design: Draft is the initial state; Active means work is
    /// underway; Ready/Conflict are end-of-run outcomes; Archived is terminal
    /// (but reversible via un-archive into Active).
    let canTransition (from: WorkspaceStatus) (to': WorkspaceStatus) : bool =
        match from, to' with
        | WorkspaceStatus.Draft, WorkspaceStatus.Active -> true
        | WorkspaceStatus.Active,
          (WorkspaceStatus.Ready | WorkspaceStatus.Conflict | WorkspaceStatus.Archived | WorkspaceStatus.Open) -> true
        | WorkspaceStatus.Open,
          (WorkspaceStatus.Ready | WorkspaceStatus.Conflict | WorkspaceStatus.Archived | WorkspaceStatus.Active) -> true
        | WorkspaceStatus.Ready, (WorkspaceStatus.Active | WorkspaceStatus.Archived | WorkspaceStatus.Open) -> true
        | WorkspaceStatus.Conflict, (WorkspaceStatus.Active | WorkspaceStatus.Archived) -> true
        | WorkspaceStatus.Archived, WorkspaceStatus.Active -> true
        | a, b when a = b -> true // idempotent: re-asserting current state is allowed
        | _ -> false

// ============================================================================
//  Conversation event-sourcing
// ============================================================================

module Conversation =
    /// An empty conversation rooted in the given workspace.
    let empty (id: ConversationId) (workspaceId: WorkspaceId) (title: string) : Conversation =
        { Id = id
          WorkspaceId = workspaceId
          Title = title
          Messages = [||]
          LastSequence = 0 }

    /// Append a single event to a conversation. Pure — returns a new value.
    let applyEvent (conv: Conversation) (event: MessageEvent) : Conversation =
        { conv with
            Messages = Array.append conv.Messages [| event |]
            LastSequence = conv.LastSequence + 1 }

    /// Replay a sequence of events from the empty conversation. Equivalent to
    /// <c>events |> Array.fold applyEvent (empty ...)</c>.
    let replay
        (id: ConversationId)
        (workspaceId: WorkspaceId)
        (title: string)
        (events: MessageEvent seq)
        : Conversation =
        events |> Seq.fold applyEvent (empty id workspaceId title)
