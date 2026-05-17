using System.Linq;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class MainViewModelTests
{
    private static MainViewModel MakeVm() => new(Composition.buildStubServices());

    [Fact]
    public void Title_DefaultsToAvelia()
    {
        var vm = MakeVm();
        Assert.Equal("Avelia", vm.Title);
    }

    [Fact]
    public void Default_RailIsExpanded()
    {
        var vm = MakeVm();
        Assert.True(vm.IsRailExpanded);
    }

    [Fact]
    public void ToggleRail_FlipsState()
    {
        var vm = MakeVm();
        var initial = vm.IsRailExpanded;
        vm.ToggleRailCommand.Execute(null);
        Assert.Equal(!initial, vm.IsRailExpanded);
    }

    [Fact]
    public async Task InitializeAsync_PopulatesRepoTreeAndInbox()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        Assert.Equal(DesignData.repositories.Count, vm.RepoGroups.Count);
        Assert.Equal(DesignData.inboxItems.Count, vm.InboxCount);
    }

    [Fact]
    public async Task InitializeAsync_OpensFirstWorkspaceAsActiveTab()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        Assert.NotNull(vm.ActiveTab);
        Assert.Single(vm.OpenTabs);
    }

    [Fact]
    public async Task OpenWorkspace_TwiceDoesNotDuplicateTab()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var existingTabCount = vm.OpenTabs.Count;
        var firstId = vm.OpenTabs[0].Id;

        await vm.OpenWorkspaceCommand.ExecuteAsync(firstId);

        Assert.Equal(existingTabCount, vm.OpenTabs.Count);
    }

    [Fact]
    public async Task OpenWorkspace_DifferentIdAddsANewTab()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var initialCount = vm.OpenTabs.Count;

        // Find a workspace ID that's not in the open tabs yet.
        var unopenedId = DesignData.workspaces
            .First(w => vm.OpenTabs.All(t => !t.Id.Equals(w.Id)))
            .Id;

        await vm.OpenWorkspaceCommand.ExecuteAsync(unopenedId);

        Assert.Equal(initialCount + 1, vm.OpenTabs.Count);
        Assert.NotNull(vm.ActiveTab);
        Assert.Equal(unopenedId, vm.ActiveTab!.Id);
    }

    [Fact]
    public async Task CloseTab_ClosingActiveTabActivatesAdjacent()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        // Open a second workspace so we have something to fall back to.
        var secondWorkspace = DesignData.workspaces
            .First(w => vm.OpenTabs.All(t => !t.Id.Equals(w.Id)));
        await vm.OpenWorkspaceCommand.ExecuteAsync(secondWorkspace.Id);

        // Active is now the second tab (most recently opened).
        var active = vm.ActiveTab;
        Assert.NotNull(active);

        vm.CloseTabCommand.Execute(active);

        Assert.NotNull(vm.ActiveTab);
        Assert.NotEqual(active, vm.ActiveTab);
    }

    [Fact]
    public async Task CloseTab_ClosingLastTabNullsActiveTab()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        while (vm.OpenTabs.Count > 0)
        {
            vm.CloseTabCommand.Execute(vm.OpenTabs[0]);
        }
        Assert.Null(vm.ActiveTab);
    }

    [Fact]
    public void NavigateSection_UpdatesActiveSection()
    {
        var vm = MakeVm();
        vm.NavigateSectionCommand.Execute(NavRailSection.Inbox);
        Assert.Equal(NavRailSection.Inbox, vm.ActiveSection);
    }
}
