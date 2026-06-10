namespace Avelia.Persistence

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avelia.Core
open Avelia.Core.Abstractions

// ----------------------------------------------------------------------------
//  In-memory persistence stores
//
//  Dictionary-backed, lock-guarded implementations of the store interfaces.
//  The source of truth until the SQLite stores (B-11) replace them behind the
//  same interfaces — `InMemoryStores.create` is the single construction point
//  that composition swaps when durable persistence lands.
// ----------------------------------------------------------------------------

type InMemoryRepositoryStore() =
    let gate = obj ()
    let store = Dictionary<RepositoryId, Repository>()

    interface IRepositoryStore with
        member _.ListAsync(ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () -> store.Values |> Seq.toArray :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                match store.TryGetValue id with
                | true, r -> Success r
                | _ -> Failure(AveliaError.NotFound(sprintf "repository:%O" id)))
            |> Task.FromResult

        member _.UpsertAsync(repo, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> store.[repo.Id] <- repo)
            Task.FromResult(Success())

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> store.Remove id |> ignore)
            Task.FromResult(Success())

type InMemoryWorkspaceStore() =
    let gate = obj ()
    let store = Dictionary<WorkspaceId, WorkspaceRecord>()

    interface IWorkspaceStore with
        member _.ListAllAsync(ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () -> store.Values |> Seq.toArray :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.ListByRepoAsync(repoId, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                store.Values |> Seq.filter (fun r -> r.Workspace.RepoId = repoId) |> Seq.toArray :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                match store.TryGetValue id with
                | true, r -> Success r
                | _ -> Failure(AveliaError.NotFound(sprintf "workspace:%O" id)))
            |> Task.FromResult

        member _.UpsertAsync(record, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> store.[record.Workspace.Id] <- record)
            Task.FromResult(Success())

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> store.Remove id |> ignore)
            Task.FromResult(Success())

type InMemoryConversationStore() =
    let gate = obj ()
    let store = Dictionary<ConversationId, Conversation>()

    interface IConversationStore with
        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                match store.TryGetValue id with
                | true, c -> Success c
                | _ -> Failure(AveliaError.NotFound(sprintf "conversation:%O" id)))
            |> Task.FromResult

        member _.GetByWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                match store.Values |> Seq.tryFind (fun c -> c.WorkspaceId = workspaceId) with
                | Some c -> Success c
                | None -> Failure(AveliaError.NotFound(sprintf "conversation-for-workspace:%O" workspaceId)))
            |> Task.FromResult

        member _.CreateAsync(conversation, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> store.[conversation.Id] <- conversation)
            Task.FromResult(Success())

        member _.AppendEventAsync(id, event, ct) =
            ct.ThrowIfCancellationRequested()

            lock gate (fun () ->
                match store.TryGetValue id with
                | true, conv ->
                    let updated = Conversation.applyEvent conv event
                    store.[id] <- updated
                    Success updated
                | _ -> Failure(AveliaError.NotFound(sprintf "conversation:%O" id)))
            |> Task.FromResult

type InMemorySettingsStore(initial: AppearanceSettings) =
    let gate = obj ()
    let mutable current = initial

    interface ISettingsStore with
        member _.LoadAsync(ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(lock gate (fun () -> current))

        member _.SaveAsync(settings, ct) =
            ct.ThrowIfCancellationRequested()
            lock gate (fun () -> current <- settings)
            Task.FromResult(Success())

/// Bundle of the four stores. Composition takes this and threads each store
/// into the real services; the SQLite swap (B-11) replaces only
/// <c>InMemoryStores.create</c> with a SQLite-backed equivalent.
type Stores =
    { Repositories: IRepositoryStore
      Workspaces: IWorkspaceStore
      Conversations: IConversationStore
      Settings: ISettingsStore }

module InMemoryStores =
    let create (initialSettings: AppearanceSettings) : Stores =
        { Repositories = InMemoryRepositoryStore()
          Workspaces = InMemoryWorkspaceStore()
          Conversations = InMemoryConversationStore()
          Settings = InMemorySettingsStore initialSettings }
