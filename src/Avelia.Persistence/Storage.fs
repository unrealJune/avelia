namespace Avelia.Persistence

open System.IO

module Storage =
    let private aveliaRoot () =
        let appData =
            System.Environment.GetFolderPath System.Environment.SpecialFolder.LocalApplicationData

        Path.Combine(appData, "Avelia")

    let defaultDbPath () = Path.Combine(aveliaRoot (), "avelia.db")

    /// Root directory under which per-workspace git worktrees are materialized
    /// (<c>%LOCALAPPDATA%/Avelia/worktrees</c>). Kept outside any repository so
    /// nested-worktree confusion can't arise.
    let worktreesRoot () = Path.Combine(aveliaRoot (), "worktrees")
