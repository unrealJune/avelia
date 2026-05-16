namespace Avelia.Core.Abstractions

open System

type TaskId = TaskId of Guid
type ProjectId = ProjectId of Guid
type SessionId = SessionId of Guid
type PullRequestId = PullRequestId of int

module TaskId =
    let create () = TaskId(Guid.NewGuid())
    let value (TaskId g) = g

module ProjectId =
    let create () = ProjectId(Guid.NewGuid())
    let value (ProjectId g) = g

module SessionId =
    let create () = SessionId(Guid.NewGuid())
    let value (SessionId g) = g
