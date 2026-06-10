namespace Avelia.Services

open System.Collections.Generic
open System.Threading.Tasks
open Avelia.Core.Abstractions

/// Real <c>IDiffService</c> for the workspace "Changes" / "Files" view:
/// resolves the workspace's worktree and reads its working-tree diff vs
/// <c>HEAD</c> (tracked changes + untracked files) via <c>IGitInspection</c>,
/// so edits an agent makes in the worktree surface in the right pane. PR-diff
/// and per-file hunk surfaces stay empty pending the GitHub diff wiring.
type DiffService(workspaces: IWorkspaceStore, inspection: IGitInspection) =
    let emptyFiles = ([||]: DiffFile[]) :> IReadOnlyList<DiffFile>
    let emptyHunks = ([||]: DiffHunk[]) :> IReadOnlyList<DiffHunk>

    interface IDiffService with
        member _.GetWorkspaceDiffAsync(workspaceId, ct) =
            task {
                match! workspaces.GetAsync(workspaceId, ct) with
                | Failure _ -> return emptyFiles
                | Success record ->
                    match! inspection.DiffAsync(record.WorktreePath, ct) with
                    | Success files -> return files
                    | Failure _ -> return emptyFiles
            }

        member _.GetPullRequestDiffAsync(_prId, _ct) = Task.FromResult emptyFiles
        member _.GetHunksAsync(_prId, _file, _ct) = Task.FromResult emptyHunks
