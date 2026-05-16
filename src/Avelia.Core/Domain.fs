namespace Avelia.Core

open System
open Avelia.Core.Abstractions

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
    let canTransition (from: TaskStatus) (to': TaskStatus) =
        match from, to' with
        | Drafting, Active -> true
        | Active, (Blocked _ | InReview _ | Abandoned _) -> true
        | InReview _, (Merged _ | Active) -> true
        | Merged _, Archived -> true
        | _ -> false
