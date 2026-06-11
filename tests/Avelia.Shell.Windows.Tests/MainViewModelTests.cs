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
    public async Task SetWorkspaceAgentWorking_MarksTabAndRailWorkingWithSpinner()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var tab = vm.OpenTabs[0];
        var item = vm.RepoGroups.SelectMany(g => g.Workspaces).First(w => w.Id.Equals(id));

        vm.SetWorkspaceAgentWorking(id, true);

        Assert.True(tab.IsAgentWorking);
        Assert.True(item.IsAgentWorking);
        Assert.Equal(WorkspaceStatus.Working, tab.Status);
        Assert.Equal(WorkspaceStatus.Working, item.Status);
    }

    [Fact]
    public async Task SetWorkspaceAgentWorking_False_KeepsYellowDotButStopsSpinner()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var tab = vm.OpenTabs[0];

        vm.SetWorkspaceAgentWorking(id, true);
        vm.SetWorkspaceAgentWorking(id, false);

        // Turn ended → spinner off, but the dot stays Working (yellow) until merge.
        Assert.False(tab.IsAgentWorking);
        Assert.Equal(WorkspaceStatus.Working, tab.Status);
    }

    [Fact]
    public async Task SetWorkspaceMerged_ClearsWorkingDotAndSpinner()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var tab = vm.OpenTabs[0];
        var item = vm.RepoGroups.SelectMany(g => g.Workspaces).First(w => w.Id.Equals(id));

        vm.SetWorkspaceAgentWorking(id, true);
        vm.SetWorkspaceMerged(id);

        Assert.False(tab.IsAgentWorking);
        Assert.False(item.IsAgentWorking);
        Assert.Equal(WorkspaceStatus.Ready, tab.Status);
        Assert.Equal(WorkspaceStatus.Ready, item.Status);
    }

    [Fact]
    public async Task SetWorkspaceTitle_UpdatesTabAndRailDisplayName()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var tab = vm.OpenTabs[0];
        var item = vm.RepoGroups.SelectMany(g => g.Workspaces).First(w => w.Id.Equals(id));

        vm.SetWorkspaceTitle(id, "Add MCP Server");

        Assert.Equal("Add MCP Server", tab.Title);
        Assert.Equal("Add MCP Server", item.Title);
        Assert.Equal("Add MCP Server", tab.DisplayName);
        Assert.Equal("Add MCP Server", item.DisplayName);
    }

    [Fact]
    public async Task SetWorkspaceTitle_EmptyTitle_FallsBackToBranchInDisplayName()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var tab = vm.OpenTabs[0];

        vm.SetWorkspaceTitle(id, "");

        Assert.Equal(tab.Branch, tab.DisplayName);
    }

    [Fact]
    public async Task RefreshWorkspaceStatuses_DoesNotClobberWorkingDot()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var id = vm.OpenTabs[0].Id;
        var item = vm.RepoGroups.SelectMany(g => g.Workspaces).First(w => w.Id.Equals(id));

        vm.SetWorkspaceAgentWorking(id, true);
        await vm.RefreshWorkspaceStatusesAsync(System.Threading.CancellationToken.None);

        Assert.Equal(WorkspaceStatus.Working, item.Status);
    }

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
    public async Task CreateWorkspaceAsync_AddsToGroupAndOpensTab()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var group = vm.RepoGroups.First();
        var before = group.Workspaces.Count;

        var result = await vm.CreateWorkspaceAsync(group.Id, "feature/new-thing");

        Assert.True(result.IsSuccess);
        Assert.Equal(before + 1, group.Workspaces.Count);
        Assert.Equal(result.Value.Id, vm.ActiveTab!.Id);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_RejectsAnInvalidBranchName()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();
        var group = vm.RepoGroups.First();

        var result = await vm.CreateWorkspaceAsync(group.Id, "bad branch with spaces");

        Assert.False(result.IsSuccess);
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
        var unopenedId = DesignData
            .workspaces.First(w => vm.OpenTabs.All(t => !t.Id.Equals(w.Id)))
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
        var secondWorkspace = DesignData.workspaces.First(w =>
            vm.OpenTabs.All(t => !t.Id.Equals(w.Id))
        );
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

    [Fact]
    public async Task CycleTab_ForwardAdvancesAndWrapsAround()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        // Ensure at least two tabs are open.
        var second = DesignData.workspaces.First(w => vm.OpenTabs.All(t => !t.Id.Equals(w.Id)));
        await vm.OpenWorkspaceCommand.ExecuteAsync(second.Id);

        Assert.True(vm.OpenTabs.Count >= 2);

        // Start from the first tab and walk forward through every tab, ending
        // back where we started (wrap-around).
        vm.ActiveTab = vm.OpenTabs[0];
        for (var i = 1; i < vm.OpenTabs.Count; i++)
        {
            vm.CycleTabCommand.Execute(true);
            Assert.Equal(vm.OpenTabs[i], vm.ActiveTab);
        }

        vm.CycleTabCommand.Execute(true);
        Assert.Equal(vm.OpenTabs[0], vm.ActiveTab);
    }

    [Fact]
    public async Task CycleTab_BackwardFromFirstWrapsToLast()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        var second = DesignData.workspaces.First(w => vm.OpenTabs.All(t => !t.Id.Equals(w.Id)));
        await vm.OpenWorkspaceCommand.ExecuteAsync(second.Id);

        vm.ActiveTab = vm.OpenTabs[0];
        vm.CycleTabCommand.Execute(false);

        Assert.Equal(vm.OpenTabs[^1], vm.ActiveTab);
    }

    [Fact]
    public async Task CycleTab_SingleTabIsNoOp()
    {
        var vm = MakeVm();
        await vm.InitializeAsync();

        while (vm.OpenTabs.Count > 1)
        {
            vm.CloseTabCommand.Execute(vm.OpenTabs[^1]);
        }

        var active = vm.ActiveTab;
        vm.CycleTabCommand.Execute(true);
        Assert.Equal(active, vm.ActiveTab);

        vm.CycleTabCommand.Execute(false);
        Assert.Equal(active, vm.ActiveTab);
    }
}
