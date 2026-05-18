using System;
using System.Linq;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class InboxViewModelTests
{
    private static InboxViewModel MakeVm()
    {
        var services = Composition.buildStubServices();
        return new InboxViewModel(services);
    }

    [Fact]
    public async Task LoadAsync_PopulatesItemsFromService()
    {
        var vm = MakeVm();
        await vm.LoadAsync();

        Assert.Equal(DesignData.inboxItems.Count, vm.Items.Count);
        Assert.Equal(DesignData.inboxItems.Count, vm.TotalCount);
        // Until persistence tracks read state every seeded item is unread.
        Assert.Equal(DesignData.inboxItems.Count, vm.UnreadCount);
        Assert.Equal($"{vm.UnreadCount} unread · {vm.TotalCount} total", vm.HeaderSubtitle);
    }

    [Fact]
    public async Task LoadAsync_ProjectsSeededFieldsOntoRows()
    {
        var vm = MakeVm();
        await vm.LoadAsync();

        for (var i = 0; i < DesignData.inboxItems.Count; i++)
        {
            var seed = DesignData.inboxItems[i];
            var row = vm.Items[i];
            Assert.Equal(seed.Title, row.Title);
            Assert.Equal(seed.Description, row.Description);
            Assert.Equal(seed.TimeAgo, row.TimeAgo);
            Assert.Equal(seed.Kind, row.Kind);
            Assert.True(row.HasLinkedWorkspace);
        }
    }

    [Fact]
    public async Task OpenCommand_WithLinkedWorkspace_RaisesWorkspaceOpenRequested()
    {
        var vm = MakeVm();
        await vm.LoadAsync();

        WorkspaceId? observed = null;
        vm.WorkspaceOpenRequested += (_, id) => observed = id;

        var target = vm.Items.First();
        vm.OpenCommand.Execute(target);

        Assert.NotNull(observed);
        Assert.Equal(target.LinkedWorkspaceId, observed);
    }

    [Fact]
    public void OpenCommand_NullItem_DoesNothing()
    {
        var vm = MakeVm();
        var fired = false;
        vm.WorkspaceOpenRequested += (_, _) => fired = true;

        vm.OpenCommand.Execute(null);

        Assert.False(fired);
    }

    [Fact]
    public void OpenCommand_WithoutLinkedWorkspace_DoesNothing()
    {
        var vm = MakeVm();
        var fired = false;
        vm.WorkspaceOpenRequested += (_, _) => fired = true;

        var unlinked = new InboxItemViewModel(
            new InboxItem(
                Guid.NewGuid(),
                "no-link",
                "",
                "0m",
                InboxItemKind.Info,
                WorkspaceId.NewWorkspaceId(Guid.Empty)
            )
        );
        Assert.False(unlinked.HasLinkedWorkspace);

        vm.OpenCommand.Execute(unlinked);

        Assert.False(fired);
    }

    [Fact]
    public async Task LoadAsync_TwiceInARow_LastLoadWins()
    {
        var vm = MakeVm();

        // Two near-simultaneous loads; the second must produce the canonical
        // state (mirrors the PrReview last-load-wins regression guard).
        var first = vm.LoadAsync();
        var second = vm.LoadAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(DesignData.inboxItems.Count, vm.Items.Count);
    }

    [Fact]
    public void HeaderSubtitle_BeforeLoad_IsEmpty()
    {
        var vm = MakeVm();
        Assert.Equal(string.Empty, vm.HeaderSubtitle);
    }
}
