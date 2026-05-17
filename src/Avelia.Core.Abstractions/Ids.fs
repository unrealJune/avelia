namespace Avelia.Core.Abstractions

open System

/// Identity of an agent task. (Legacy; predates the design — preserved for the
/// existing <c>Task</c> domain type in <c>Avelia.Core/Domain.fs</c>.)
type TaskId = TaskId of Guid

/// Identity of a project (currently unused, kept for forward-compat).
type ProjectId = ProjectId of Guid

/// Identity of a Claude agent session.
type SessionId = SessionId of Guid

/// Number of a GitHub pull request. Kept as <c>int</c> to match upstream APIs.
type PullRequestId = PullRequestId of int

/// Identity of a repository tracked by Avelia.
type RepositoryId = RepositoryId of Guid

/// Identity of an agent workspace (a branch + worktree the agent owns).
type WorkspaceId = WorkspaceId of Guid

/// Identity of a chat-style conversation within a workspace.
type ConversationId = ConversationId of Guid

/// Identity of a single message event inside a conversation.
type MessageId =
    | MessageId of Guid

    /// C#-friendly accessor — equivalent to <c>MessageId.value</c> in F#.
    member this.Value = let (MessageId g) = this in g

/// Identity of a long-running command spawned by a workspace (e.g. <c>pnpm dev</c>).
type RunId = RunId of Guid

module TaskId =
    let create () = TaskId(Guid.NewGuid())
    let value (TaskId g) = g

module ProjectId =
    let create () = ProjectId(Guid.NewGuid())
    let value (ProjectId g) = g

module SessionId =
    let create () = SessionId(Guid.NewGuid())
    let value (SessionId g) = g

module RepositoryId =
    let create () = RepositoryId(Guid.NewGuid())
    let value (RepositoryId g) = g

module WorkspaceId =
    let create () = WorkspaceId(Guid.NewGuid())
    let value (WorkspaceId g) = g

module ConversationId =
    let create () = ConversationId(Guid.NewGuid())
    let value (ConversationId g) = g

module MessageId =
    let create () = MessageId(Guid.NewGuid())
    let value (MessageId g) = g

module RunId =
    let create () = RunId(Guid.NewGuid())
    let value (RunId g) = g
