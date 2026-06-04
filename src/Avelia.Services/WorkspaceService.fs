namespace Avelia.Services

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avelia.Core
open Avelia.Core.Abstractions

/// Real <c>IWorkspaceService</c>. A workspace is a git worktree + branch + a
/// bound conversation; <c>CreateAsync</c> materializes all three.
///
/// Reads project out <c>WorkspaceRecord.Workspace</c> for the shell (the
/// worktree path stays internal). <c>ArchiveAsync</c> tears down any running
/// agent session for the workspace's conversation via the injected
/// <paramref name="disposeConversation"/> delegate — a delegate rather than the
/// orchestrator itself, so the two services don't form a construction cycle.
type WorkspaceService
    (
        workspaces: IWorkspaceStore,
        repositories: IRepositoryStore,
        conversations: IConversationStore,
        settings: ISettingsStore,
        git: IGitOperations,
        worktreesRoot: string,
        now: unit -> DateTimeOffset,
        disposeConversation: ConversationId -> Task<unit>
    ) =

    /// Filesystem-safe single path segment from an arbitrary branch name.
    let sanitize (s: string) =
        let invalid = Set.ofArray (Path.GetInvalidFileNameChars())

        String(s |> Seq.map (fun c -> if c = '/' || c = '\\' || invalid.Contains c then '-' else c) |> Seq.toArray)

    let worktreePathFor (repo: Repository) (branch: BranchName) (wsId: WorkspaceId) =
        // Unique per workspace id so `git worktree add` never hits an existing
        // path; grouped by repo + branch for human legibility.
        let shortId = (string (WorkspaceId.value wsId)).Substring(0, 8)
        let dir = Path.Combine(worktreesRoot, sanitize repo.Name, sanitize branch.Value + "-" + shortId)
        RepoPath.Create dir

    let project (records: IReadOnlyList<WorkspaceRecord>) : IReadOnlyList<Workspace> =
        records |> Seq.map (fun r -> r.Workspace) |> Seq.toArray :> IReadOnlyList<_>

    interface IWorkspaceService with
        member _.ListAllAsync(ct) =
            task {
                let! records = workspaces.ListAllAsync ct
                return project records
            }

        member _.ListByRepoAsync(repoId, ct) =
            task {
                let! records = workspaces.ListByRepoAsync(repoId, ct)
                return project records
            }

        member _.GetAsync(id, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Success r -> return Success r.Workspace
                | Failure e -> return Failure e
            }

        member _.CreateAsync(repoId, branch, baseBranch, ct) =
            task {
                match! repositories.GetAsync(repoId, ct) with
                | Failure e -> return Failure e
                | Success repo ->
                    let wsId = WorkspaceId.create ()
                    let convId = ConversationId.create ()
                    let worktreePath = worktreePathFor repo branch wsId

                    match! git.WorktreeAddAsync(repo.Path, branch, worktreePath, ct) with
                    | Failure e -> return Failure e
                    | Success _ ->
                        let! current = settings.LoadAsync ct
                        let title = sprintf "%s / %s" repo.Name branch.Value
                        let conv = Conversation.empty convId wsId title

                        match! conversations.CreateAsync(conv, ct) with
                        | Failure e -> return Failure e
                        | Success() ->
                            let ws: Workspace =
                                { Id = wsId
                                  RepoId = repoId
                                  Branch = branch
                                  Base = baseBranch
                                  Status = WorkspaceStatus.Draft
                                  DiffAdd = 0
                                  DiffDel = 0
                                  Agent = current.DefaultModel
                                  LastUpdated = now ()
                                  LastUpdatedDisplay = "just now"
                                  PrNumber = 0 }

                            let record =
                                { Workspace = ws
                                  WorktreePath = worktreePath
                                  ConversationId = convId }

                            match! workspaces.UpsertAsync(record, ct) with
                            | Success() -> return Success ws
                            | Failure e -> return Failure e
            }

        member _.ArchiveAsync(id, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    if Workspace.canTransition record.Workspace.Status WorkspaceStatus.Archived then
                        // Stop any running agent session before flipping state.
                        do! disposeConversation record.ConversationId

                        let updated =
                            { record with
                                Workspace =
                                    { record.Workspace with
                                        Status = WorkspaceStatus.Archived } }

                        match! workspaces.UpsertAsync(updated, ct) with
                        | Success() -> return Success()
                        | Failure e -> return Failure e
                    else
                        return Failure(AveliaError.Conflict(sprintf "Cannot archive from %A" record.Workspace.Status))
            }
