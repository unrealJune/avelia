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
        inspection: IGitInspection,
        worktreesRoot: string,
        now: unit -> DateTimeOffset,
        disposeConversation: ConversationId -> Task<unit>
    ) =

    /// Filesystem-safe single path segment from an arbitrary branch name.
    let sanitize (s: string) =
        let invalid = Set.ofArray (Path.GetInvalidFileNameChars())

        String(
            s
            |> Seq.map (fun c -> if c = '/' || c = '\\' || invalid.Contains c then '-' else c)
            |> Seq.toArray
        )

    let worktreePathFor (repo: Repository) (branch: BranchName) (wsId: WorkspaceId) =
        // Unique per workspace id so `git worktree add` never hits an existing
        // path; grouped by repo + branch for human legibility.
        let shortId = (string (WorkspaceId.value wsId)).Substring(0, 8)

        let dir =
            Path.Combine(worktreesRoot, sanitize repo.Name, sanitize branch.Value + "-" + shortId)

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
                    // Auto-name: an empty BranchName sentinel (the codebase's
                    // empty form, e.g. Unchecked.defaultof<BranchName>) means
                    // "pick an unused rail name". A caller-supplied branch is
                    // used verbatim. Pool names are curated to be valid branch
                    // names (covered by a property test), so Create is safe.
                    let! branch =
                        task {
                            if not (String.IsNullOrWhiteSpace branch.Value) then
                                return branch
                            else
                                let! existing = workspaces.ListByRepoAsync(repoId, ct)

                                let used =
                                    existing
                                    |> Seq.map (fun r -> r.Workspace.Branch.Value)
                                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                                    |> Set.ofSeq

                                return BranchName.Create(WorktreeNames.pickUnused used Random.Shared)
                        }

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
                                  PrNumber = 0
                                  ReasoningEffort = ""
                                  ContextTier = "" }

                            let record =
                                { Workspace = ws
                                  WorktreePath = worktreePath
                                  ConversationId = convId }

                            match! workspaces.UpsertAsync(record, ct) with
                            | Success() -> return Success ws
                            | Failure e -> return Failure e
            }

        member _.GetStatusAsync(id, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Failure e -> return Failure e
                | Success record -> return! inspection.StatusAsync(record.WorktreePath, ct)
            }

        member _.UpdateStatusAsync(id, status, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    if Workspace.canTransition record.Workspace.Status status then
                        let updated =
                            { record with
                                Workspace =
                                    { record.Workspace with
                                        Status = status } }

                        match! workspaces.UpsertAsync(updated, ct) with
                        | Success() -> return Success updated.Workspace
                        | Failure e -> return Failure e
                    else
                        return
                            Failure(
                                AveliaError.Conflict(
                                    sprintf "Cannot transition from %A to %A" record.Workspace.Status status
                                )
                            )
            }

        member _.SetAgentConfigAsync(id, model, reasoningEffort, contextTier, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    let updated =
                        { record with
                            Workspace =
                                { record.Workspace with
                                    Agent = model
                                    ReasoningEffort = reasoningEffort
                                    ContextTier = contextTier } }

                    match! workspaces.UpsertAsync(updated, ct) with
                    | Failure e -> return Failure e
                    | Success() ->
                        // Drop any running session so the next message restarts
                        // with the new model/thinking/context.
                        do! disposeConversation record.ConversationId
                        return Success updated.Workspace
            }

        member _.DeleteAsync(id, ct) =
            task {
                match! workspaces.GetAsync(id, ct) with
                | Failure e -> return Failure e
                | Success record ->
                    // Stop any running agent session before touching the disk.
                    do! disposeConversation record.ConversationId

                    match! git.WorktreeRemoveAsync(record.WorktreePath, true, ct) with
                    | Failure e -> return Failure e
                    | Success() ->
                        // Best-effort branch cleanup: a failure (branch checked
                        // out elsewhere, already gone) must not block dropping
                        // the record.
                        match! repositories.GetAsync(record.Workspace.RepoId, ct) with
                        | Success repo ->
                            let! _ = git.BranchDeleteAsync(repo.Path, record.Workspace.Branch, true, ct)
                            ()
                        | Failure _ -> ()

                        return! workspaces.RemoveAsync(id, ct)
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
