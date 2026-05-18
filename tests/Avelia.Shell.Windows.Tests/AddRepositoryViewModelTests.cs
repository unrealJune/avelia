using System.Linq;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class AddRepositoryViewModelTests
{
    private static AddRepositoryViewModel MakeVm(out AveliaServices services)
    {
        services = Composition.buildStubServices();
        return new AddRepositoryViewModel(services);
    }

    [Fact]
    public void AddCommand_Disabled_WhenLocalPathEmpty()
    {
        var vm = MakeVm(out _);
        Assert.Equal(AddRepositoryViewModel.Tab.LocalFolder, vm.ActiveTab);
        Assert.False(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void AddCommand_EnablesWhenLocalPathValid()
    {
        var vm = MakeVm(out _);
        vm.LocalPath = @"C:\work\demo-repo";
        Assert.True(vm.AddCommand.CanExecute(null));
        Assert.False(vm.HasLocalPathError);
    }

    [Fact]
    public void LocalPathError_PopulatedForTraversalPath()
    {
        var vm = MakeVm(out _);
        vm.LocalPath = @"C:\work\..\etc";
        Assert.True(vm.HasLocalPathError);
        Assert.False(vm.AddCommand.CanExecute(null));
        Assert.Contains("..", vm.LocalPathError);
    }

    [Fact]
    public async Task AddCommand_Local_InvokesRepositoryServiceAndRaisesEvent()
    {
        var vm = MakeVm(out var services);
        await vm.LoadAsync();

        var existing = (await services.Repositories.ListAsync(CancellationToken.None)).Count;
        var targetPath = @"C:\work\brand-new-repo";
        vm.LocalPath = targetPath;

        Repository? added = null;
        vm.RepositoryAdded += (_, r) => added = r;
        await vm.AddCommand.ExecuteAsync(null);

        Assert.NotNull(added);
        Assert.Equal(targetPath, added!.Path.Value);
        Assert.Equal("brand-new-repo", added.Name);
        Assert.False(vm.HasError);

        var newCount = (await services.Repositories.ListAsync(CancellationToken.None)).Count;
        Assert.Equal(existing + 1, newCount);
    }

    [Fact]
    public void GitUrl_SshDetectionTogglesInfoBar()
    {
        var vm = MakeVm(out _);
        vm.ActiveTab = AddRepositoryViewModel.Tab.GitUrl;

        vm.GitUrl = "https://github.com/owner/repo.git";
        Assert.False(vm.IsSshUrl);

        vm.GitUrl = "git@github.com:owner/repo.git";
        Assert.True(vm.IsSshUrl);

        vm.GitUrl = "ssh://git@github.com/owner/repo.git";
        Assert.True(vm.IsSshUrl);
    }

    [Fact]
    public void AddCommand_GitUrl_RequiresBothUrlAndCloneTarget()
    {
        var vm = MakeVm(out _);
        vm.ActiveTab = AddRepositoryViewModel.Tab.GitUrl;
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.GitUrl = "git@github.com:owner/repo.git";
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.CloneToPath = @"C:\work\repo";
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void AddCommand_GitHub_DisabledWithoutSelection()
    {
        var vm = MakeVm(out _);
        vm.ActiveTab = AddRepositoryViewModel.Tab.GitHub;
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.SelectedGitHubRepo = new GitHubRepoItem("owner", "repo", "desc");
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_PopulatesRecentReposFromService()
    {
        var vm = MakeVm(out var services);
        await vm.LoadAsync();

        var expected = (await services.Repositories.ListAsync(CancellationToken.None)).Count;
        Assert.Equal(expected, vm.RecentRepos.Count);
        Assert.All(vm.RecentRepos, r => Assert.False(string.IsNullOrEmpty(r.Path)));
    }

    [Fact]
    public async Task PickRecent_FillsLocalPathAndSwitchesToLocalTab()
    {
        var vm = MakeVm(out _);
        vm.ActiveTab = AddRepositoryViewModel.Tab.GitUrl;
        await vm.LoadAsync();

        var first = vm.RecentRepos[0];
        vm.PickRecent(first);

        Assert.Equal(AddRepositoryViewModel.Tab.LocalFolder, vm.ActiveTab);
        Assert.Equal(first.Path, vm.LocalPath);
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void ActiveTabChange_ClearsErrorBanner()
    {
        var vm = MakeVm(out _);
        // Force an error via the path validator.
        vm.LocalPath = @"C:\..\bad";
        vm.AddCommand.Execute(null);

        // Switching tabs clears the bar (it's specific to the previous mode).
        vm.ActiveTab = AddRepositoryViewModel.Tab.GitUrl;
        Assert.False(vm.HasError);
    }
}

public class MainViewModelAppendRepositoryTests
{
    [Fact]
    public void AppendRepository_AddsGroupToRail()
    {
        var services = Composition.buildStubServices();
        var vm = new MainViewModel(services);
        var initial = vm.RepoGroups.Count;

        var repo = new Repository(
            RepositoryId.NewRepositoryId(System.Guid.NewGuid()),
            "fresh-repo",
            RepoPath.Create(@"C:\work\fresh-repo"),
            BranchName.Create("main"),
            true
        );
        vm.AppendRepository(repo);

        Assert.Equal(initial + 1, vm.RepoGroups.Count);
        Assert.Contains(vm.RepoGroups, g => g.Id.Equals(repo.Id));
    }

    [Fact]
    public void AppendRepository_IsIdempotentOnSameId()
    {
        var services = Composition.buildStubServices();
        var vm = new MainViewModel(services);
        var repo = new Repository(
            RepositoryId.NewRepositoryId(System.Guid.NewGuid()),
            "demo",
            RepoPath.Create(@"C:\work\demo"),
            BranchName.Create("main"),
            true
        );
        vm.AppendRepository(repo);
        var afterFirst = vm.RepoGroups.Count;
        vm.AppendRepository(repo);
        Assert.Equal(afterFirst, vm.RepoGroups.Count);
    }

    [Fact]
    public void OpenAddRepoDialogCommand_RaisesRequestedEvent()
    {
        var services = Composition.buildStubServices();
        var vm = new MainViewModel(services);
        var fired = 0;
        vm.OpenAddRepoDialogRequested += (_, _) => fired++;

        vm.OpenAddRepoDialogCommand.Execute(null);

        Assert.Equal(1, fired);
    }
}
