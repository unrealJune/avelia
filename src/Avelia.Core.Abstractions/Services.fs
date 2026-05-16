namespace Avelia.Core.Abstractions

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type ITaskService =
    abstract ListAsync: CancellationToken -> Task<IReadOnlyList<TaskId>>

type IVcsService =
    abstract CurrentBranchAsync: CancellationToken -> Task<string>

type IAgentService =
    abstract StartSessionAsync: prompt: string * CancellationToken -> Task<SessionId>
