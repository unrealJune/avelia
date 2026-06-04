namespace Avelia.Services

open System.Threading.Tasks
open Avelia.Core.Abstractions

/// Real <c>IRepositoryService</c>: persists to an <c>IRepositoryStore</c> and
/// validates a path is a real git repository (via <c>IGitInspection</c>) before
/// adding it — a status read fails on a non-repo, surfacing as the validation
/// error the AddRepository dialog renders.
type RepositoryService(store: IRepositoryStore, inspection: IGitInspection) =

    let deriveName (path: RepoPath) =
        let p = path.Value.Replace('\\', '/').TrimEnd('/')
        let i = p.LastIndexOf '/'
        if i < 0 then p else p.Substring(i + 1)

    interface IRepositoryService with
        member _.ListAsync(ct) = store.ListAsync ct
        member _.GetAsync(id, ct) = store.GetAsync(id, ct)

        member _.AddAsync(path, defaultBase, ct) =
            task {
                // Gate: a status read against a non-repository fails, so this
                // doubles as "is this actually a git working tree?".
                match! inspection.StatusAsync(path, ct) with
                | Failure e -> return Failure e
                | Success _ ->
                    let repo =
                        { Id = RepositoryId.create ()
                          Name = deriveName path
                          Path = path
                          DefaultBase = defaultBase
                          IsOpen = true }

                    match! store.UpsertAsync(repo, ct) with
                    | Success() -> return Success repo
                    | Failure e -> return Failure e
            }

        member _.RemoveAsync(id, ct) = store.RemoveAsync(id, ct)
