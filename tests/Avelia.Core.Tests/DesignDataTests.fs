module Avelia.Core.Tests.DesignDataTests

open Xunit
open Avelia.Core
open Avelia.Core.Abstractions

// Smoke tests on DesignData — guard against silent regressions when the
// seed values are edited (counts, IDs, transcript length).

[<Fact>]
let ``DesignData has 8 repositories`` () =
    Assert.Equal(8, DesignData.repositories.Count)

[<Fact>]
let ``DesignData has 5 workspaces`` () =
    Assert.Equal(5, DesignData.workspaces.Count)

[<Fact>]
let ``Archive workspace is Ready`` () =
    let ws =
        DesignData.workspaces
        |> Seq.find (fun w -> w.Id = DesignData.archiveWorkspaceId)

    Assert.Equal(WorkspaceStatus.Ready, ws.Status)

[<Fact>]
let ``Conversation transcript has 8 events`` () =
    Assert.Equal(8, DesignData.archiveConversation.Messages.Length)

[<Fact>]
let ``Conversation LastSequence equals event count`` () =
    Assert.Equal(DesignData.archiveConversation.Messages.Length, DesignData.archiveConversation.LastSequence)

[<Fact>]
let ``Diff file list has 10 entries`` () =
    Assert.Equal(10, DesignData.diffFiles.Count)

[<Fact>]
let ``Exactly one diff file is focused`` () =
    let focused =
        DesignData.diffFiles |> Seq.filter (fun f -> f.IsFocused) |> Seq.length

    Assert.Equal(1, focused)

[<Fact>]
let ``Inbox has 4 items`` () =
    Assert.Equal(4, DesignData.inboxItems.Count)

[<Fact>]
let ``Composition.buildStubServices yields a usable Services record`` () =
    let svcs = Composition.buildStubServices ()
    Assert.NotNull(svcs.Repositories)
    Assert.NotNull(svcs.Workspaces)
    Assert.NotNull(svcs.Conversations)
    Assert.NotNull(svcs.Diffs)
    Assert.NotNull(svcs.PullRequests)
    Assert.NotNull(svcs.Runs)
    Assert.NotNull(svcs.Inbox)
