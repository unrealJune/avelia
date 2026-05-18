namespace Avelia.Core

open System
open System.Collections.Generic
open Avelia.Core.Abstractions

/// Typed equivalent of the design's <c>data.jsx</c>: 8 repositories, 5 active
/// workspaces, a sample transcript with one of each message kind, the PR file
/// list and two diff hunks, four inbox items.
///
/// This is the single source of mock data for the app; the shell binds to
/// these values via stub services. Real persistence/VCS adapters will replace
/// the stubs without the shell knowing the difference.
module DesignData =

    // ----- Fixed IDs (so stable across runs; lets tests reference specific items) -----

    let private gid (s: string) = Guid.Parse s

    let conductorRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000001")
    let meltyHomeRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000002")
    let swipeRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000003")
    let conductorDocsRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000004")
    let conductorApiRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000005")
    let chorusRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000006")
    let apiRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000007")
    let metarquizRepoId = RepositoryId(gid "11111111-1111-1111-1111-000000000008")

    let archiveWorkspaceId = WorkspaceId(gid "22222222-2222-2222-2222-000000000001")
    let trayWorkspaceId = WorkspaceId(gid "22222222-2222-2222-2222-000000000002")
    let instrWorkspaceId = WorkspaceId(gid "22222222-2222-2222-2222-000000000003")
    let agentWorkspaceId = WorkspaceId(gid "22222222-2222-2222-2222-000000000004")
    let meltyWorkspaceId = WorkspaceId(gid "22222222-2222-2222-2222-000000000005")

    let archiveConversationId =
        ConversationId(gid "33333333-3333-3333-3333-000000000001")

    let archivePrId = PullRequestId 1432

    // ----- Repositories -----

    /// Full list of repositories tracked by the workspace tree. <c>IsOpen</c>
    /// mirrors the design's initially-expanded groups (conductor + melty_home).
    let repositories: IReadOnlyList<Repository> =
        let mk id name path isOpen =
            { Id = id
              Name = name
              Path = RepoPath.Create path
              DefaultBase = BranchName.Create "main"
              IsOpen = isOpen }

        [| mk conductorRepoId "conductor" "C:/work/conductor" true
           mk meltyHomeRepoId "melty_home" "C:/work/melty_home" true
           mk swipeRepoId "swipe" "C:/work/swipe" false
           mk conductorDocsRepoId "conductor-docs" "C:/work/conductor-docs" false
           mk conductorApiRepoId "conductor_api" "C:/work/conductor_api" false
           mk chorusRepoId "chorus" "C:/work/chorus" false
           mk apiRepoId "api" "C:/work/api" false
           mk metarquizRepoId "metarquiz-2" "C:/work/metarquiz-2" false |]
        :> IReadOnlyList<_>

    // ----- Workspaces -----

    let private now = DateTimeOffset.Parse "2026-05-16T16:30:00+00:00"

    let workspaces: IReadOnlyList<Workspace> =
        [| { Id = archiveWorkspaceId
             RepoId = conductorRepoId
             Branch = BranchName.Create "archive-in-repo-details"
             Base = BranchName.Create "kampala-v3"
             Status = WorkspaceStatus.Ready
             DiffAdd = 312
             DiffDel = 332
             Agent = Sonnet45
             LastUpdated = now
             LastUpdatedDisplay = "Just now"
             PrNumber = 1432 }

           { Id = trayWorkspaceId
             RepoId = conductorRepoId
             Branch = BranchName.Create "system-tray-status"
             Base = BranchName.Create "caracas-v2"
             Status = WorkspaceStatus.Conflict
             DiffAdd = 611
             DiffDel = 1
             Agent = Opus41
             LastUpdated = now.AddMinutes(-4.0)
             LastUpdatedDisplay = "4 min ago"
             PrNumber = 0 }

           { Id = instrWorkspaceId
             RepoId = meltyHomeRepoId
             Branch = BranchName.Create "update-instructions-codex"
             Base = BranchName.Create "papeete-v1"
             Status = WorkspaceStatus.Ready
             DiffAdd = 1
             DiffDel = 1
             Agent = Sonnet45
             LastUpdated = now.AddMinutes(-12.0)
             LastUpdatedDisplay = "12 min ago"
             PrNumber = 0 }

           { Id = agentWorkspaceId
             RepoId = meltyHomeRepoId
             Branch = BranchName.Create "add-agent-workspaces-txt"
             Base = BranchName.Create "maputo-v2"
             Status = WorkspaceStatus.Archived
             DiffAdd = 1
             DiffDel = 0
             Agent = Haiku45
             LastUpdated = now.AddHours(-1.0)
             LastUpdatedDisplay = "1 h ago"
             PrNumber = 0 }

           { Id = meltyWorkspaceId
             RepoId = meltyHomeRepoId
             Branch = BranchName.Create "cbh123-melty-labs-ho"
             Base = BranchName.Create "austin"
             Status = WorkspaceStatus.Draft
             DiffAdd = 1037
             DiffDel = 96
             Agent = Sonnet45
             LastUpdated = now.AddDays(-60.0)
             LastUpdatedDisplay = "2 mo ago"
             PrNumber = 0 } |]
        :> IReadOnlyList<_>

    // ----- Conversation transcript -----

    let private mkMsgId () = MessageId.create ()

    /// The 8-event transcript for the <c>archive-in-repo-details</c> workspace.
    /// Ordered exactly as <c>data.jsx</c>:: <c>transcript</c>.
    let archiveConversation: Conversation =
        let events: MessageEvent array =
            [| AgentErrorAppended
                   { Id = mkMsgId ()
                     Text = "ReferenceError: Can't find variable: useDefaultOpenInApp in @RepositoryDetailsDialog.tsx"
                     Timestamp = now.AddMinutes(-30.0) }

               ToolBatchAppended
                   { Id = mkMsgId ()
                     ToolCount = 13
                     MessageCount = 7
                     ToolKinds = [| "files"; "search"; "terminal"; "diff" |]
                     Timestamp = now.AddMinutes(-29.0) }

               AgentMessageAppended
                   { Id = mkMsgId ()
                     Text =
                       "Perfect! I've added the missing imports. Now let me run the validation to make sure everything compiles correctly:"
                     Timestamp = now.AddMinutes(-28.0) }

               ChangeNoteAppended
                   { Id = mkMsgId ()
                     File = RelativePath.Create "src/ui/components/RepositoryDetailsDialog.tsx"
                     Add = 4
                     Del = 1
                     Timestamp = now.AddMinutes(-27.0) }

               UserMessageAppended
                   { Id = mkMsgId ()
                     Text =
                       "cool works now. i was thinking it could be nice to use a version of the @command.tsx in the @RepositoryDetailsDialog.tsx instead of trying to reinvent the wheel of searching/navigating with keyboard"
                     Refs = [| "command.tsx"; "RepositoryDetailsDialog.tsx" |]
                     Timestamp = now.AddMinutes(-20.0) }

               ToolBatchAppended
                   { Id = mkMsgId ()
                     ToolCount = 15
                     MessageCount = 13
                     ToolKinds = [| "files"; "search"; "terminal"; "diff" |]
                     Timestamp = now.AddMinutes(-15.0) }

               AgentMessageAppended
                   { Id = mkMsgId ()
                     Text = "Perfect! The refactoring is complete. Let me summarize what was done:"
                     Timestamp = now.AddMinutes(-2.0) }

               AgentMarkdownAppended
                   { Id = mkMsgId ()
                     Heading = "Summary"
                     Body =
                       "I successfully replaced the custom search and virtualized list implementation with the Command component from shadcn/ui. Here are the key improvements:"
                     Items =
                       [| { Bold = "Replaced custom search with CommandInput"
                            Detail =
                              "The manual Input component with Search icon is now a CommandInput with built-in search styling." }
                          { Bold = "Removed react-window with CommandList"
                            Detail =
                              "No more manual virtualization or ResizeObserver complexity; the Command component handles overflow and scrolling automatically." }
                          { Bold = "Keyboard navigation for free"
                            Detail = "Arrow keys, Enter, and Escape are handled by the Radix primitive." } |]
                     Timestamp = now.AddMinutes(-1.0) } |]

        Conversation.replay archiveConversationId archiveWorkspaceId "Debugging ReferenceError" events

    // ----- Diff (workspace + PR file list) -----

    let private mkDiff path add del kind focused =
        { Path = RelativePath.Create path
          Add = add
          Del = del
          Kind = kind
          IsFocused = focused }

    let diffFiles: IReadOnlyList<DiffFile> =
        [| mkDiff "src/App.tsx" 2 5 DiffKind.Modified false
           mkDiff "src/core/conductor/WorkspaceAPI.ts" 53 1 DiffKind.Modified false
           mkDiff "src/ui/components/FileBadge.tsx" 2 3 DiffKind.Modified false
           mkDiff "src/ui/components/RepositoryDetailsDialog.tsx" 225 117 DiffKind.Modified true
           mkDiff "src/ui/components/ToolRenderers.tsx" 17 2 DiffKind.Modified false
           mkDiff "src/ui/components/WorkspaceSidebar.tsx" 1 15 DiffKind.Modified false
           mkDiff "src/ui/components/git/diff/GitDiffFileHeader.tsx" 1 0 DiffKind.Modified false
           mkDiff "src/ui/components/readonly/ReadOnlyFileHeader.tsx" 3 0 DiffKind.Modified false
           mkDiff "src/ui/hooks/useWorkspaceContext.ts" 8 0 DiffKind.Modified false
           mkDiff "src/ui/pages/ArchivedWorkspacesPage.tsx" 0 189 DiffKind.Deleted false |]
        :> IReadOnlyList<_>

    // ----- Diff hunks (for PR review viewer) -----

    let private hunkLines (lines: (int * DiffLineKind * string) array) : DiffLine array =
        lines |> Array.map (fun (n, k, t) -> { LineNumber = n; Kind = k; Text = t })

    let diffHunks: IReadOnlyList<DiffHunk> =
        let dialog = RelativePath.Create "src/ui/components/RepositoryDetailsDialog.tsx"

        [| { File = dialog
             Header = "@@ -42,18 +42,28 @@"
             Lines =
               hunkLines
                   [| 42,
                      Context,
                      " import { Dialog, DialogContent, DialogHeader, DialogTitle } from \"@/ui/primitives/dialog\";"
                      43, Context, " import { Button } from \"@/ui/primitives/button\";"
                      44, Deletion, "-import { Input } from \"@/ui/primitives/input\";"
                      44, Deletion, "-import { Search, ChevronRight } from \"lucide-react\";"
                      44, Deletion, "-import { FixedSizeList } from \"react-window\";"
                      44, Addition, "+import { useDefaultOpenInApp } from \"@/ui/hooks/useDefaultOpenInApp\";"
                      45, Addition, "+import {"
                      46, Addition, "+  Command,"
                      47, Addition, "+  CommandInput,"
                      48, Addition, "+  CommandList,"
                      49, Addition, "+  CommandEmpty,"
                      50, Addition, "+  CommandGroup,"
                      51, Addition, "+  CommandItem,"
                      52, Addition, "+} from \"@/ui/primitives/command\";"
                      53, Addition, "+import { useDefaultOpenInApp } from \"@/ui/hooks/useDefaultOpenInApp\";"
                      54, Context, " "
                      55, Context, " interface RepositoryDetailsDialogProps {"
                      56, Context, "   open: boolean;"
                      57, Context, "   onClose: () => void;"
                      58, Context, " }" |] }

           { File = dialog
             Header = "@@ -118,42 +128,38 @@"
             Lines =
               hunkLines
                   [| 118,
                      Context,
                      " export function RepositoryDetailsDialog({ open, onClose }: RepositoryDetailsDialogProps) {"
                      119, Context, "   const [query, setQuery] = useState(\"\");"
                      120, Deletion, "-  const [activeIndex, setActiveIndex] = useState(0);"
                      121, Deletion, "-  const listRef = useRef<FixedSizeList | null>(null);"
                      122, Deletion, "-  const inputRef = useRef<HTMLInputElement | null>(null);"
                      123, Context, " "
                      124, Context, "   const { repositories } = useRepositories();"
                      125,
                      Context,
                      "   const filtered = useMemo(() => filterRepos(repositories, query), [repositories, query]);"
                      126, Context, " "
                      127, Deletion, "-  useEffect(() => {"
                      128, Deletion, "-    if (!open) return;"
                      129, Deletion, "-    const handler = (e: KeyboardEvent) => {"
                      130, Deletion, "-      if (e.key === \"ArrowDown\") {"
                      131, Deletion, "-        setActiveIndex((i) => Math.min(i + 1, filtered.length - 1));"
                      132, Deletion, "-      } else if (e.key === \"ArrowUp\") {"
                      133, Deletion, "-        setActiveIndex((i) => Math.max(i - 1, 0));"
                      134, Deletion, "-      }"
                      135, Deletion, "-    };"
                      136, Deletion, "-    window.addEventListener(\"keydown\", handler);"
                      137, Deletion, "-    return () => window.removeEventListener(\"keydown\", handler);"
                      138, Deletion, "-  }, [open, filtered.length]);"
                      127, Addition, "+  const handleSelect = useCallback((repoId: string) => {"
                      128, Addition, "+    onSelect(repoId);"
                      129, Addition, "+    onClose();"
                      130, Addition, "+  }, [onSelect, onClose]);" |] } |]
        :> IReadOnlyList<_>

    // ----- PR + checks for the archive workspace -----

    let archivePullRequest: PullRequest =
        { Id = archivePrId
          Number = 1432
          Title = "archive-in-repo-details"
          Branch = BranchName.Create "archive-in-repo-details"
          Base = BranchName.Create "kampala-v3"
          Status = PrStatus.Approved
          MergeReady = true
          Checks =
            [| { Name = "Build"
                 Status = CheckStatus.Passed
                 Description = "pnpm build · 1m 22s"
                 Count = "1/1" }
               { Name = "Unit tests"
                 Status = CheckStatus.Passed
                 Description = "vitest run · 24/24 passed"
                 Count = "24/24" }
               { Name = "Lint"
                 Status = CheckStatus.Passed
                 Description = "eslint . · 10 files"
                 Count = "10/10" }
               { Name = "Type check"
                 Status = CheckStatus.Passed
                 Description = "tsc --noEmit · 0 errors"
                 Count = "ok" }
               { Name = "Conflicts"
                 Status = CheckStatus.Passed
                 Description = "No conflicts with kampala-v3"
                 Count = "ok" }
               { Name = "Coverage"
                 Status = CheckStatus.Warn
                 Description = "82% — 3% below target"
                 Count = "82%" } |] }

    // ----- Appearance -----

    /// Initial appearance preferences for a freshly-installed app — Sky Blue
    /// accent, Comfortable density, transparency on, Sonnet 4.5 default model.
    /// Mirrors the defaults shown in the Settings → Appearance design.
    let defaultAppearance: AppearanceSettings =
        { Accent = AccentChoice.SkyBlue
          Density = Density.Comfortable
          Transparency = true
          OpenWithRightPanel = true
          DefaultModel = Sonnet45
          ExtendedThinking = false }

    // ----- Inbox -----

    let inboxItems: IReadOnlyList<InboxItem> =
        [| { Id = gid "44444444-4444-4444-4444-000000000001"
             Title = "system-tray-status has merge conflicts"
             Description = "caracas-v2 can no longer fast-forward — rebase or resolve to continue."
             TimeAgo = "4m"
             Kind = InboxItemKind.Warning
             LinkedWorkspaceId = trayWorkspaceId }

           { Id = gid "44444444-4444-4444-4444-000000000002"
             Title = "archive-in-repo-details ready to merge"
             Description = "All 10 checks passed. 1 reviewer approved."
             TimeAgo = "12m"
             Kind = InboxItemKind.Success
             LinkedWorkspaceId = archiveWorkspaceId }

           { Id = gid "44444444-4444-4444-4444-000000000003"
             Title = "Sonnet 4.5 finished a plan for update-instructions-codex"
             Description = "3 file edits proposed. Ready for your review."
             TimeAgo = "1h"
             Kind = InboxItemKind.Info
             LinkedWorkspaceId = instrWorkspaceId }

           { Id = gid "44444444-4444-4444-4444-000000000004"
             Title = "cbh123/melty-labs-ho… abandoned by author"
             Description = "Branch is 2 months old with 1037 unpublished changes. Archive?"
             TimeAgo = "2mo"
             Kind = InboxItemKind.Info
             LinkedWorkspaceId = meltyWorkspaceId } |]
        :> IReadOnlyList<_>
