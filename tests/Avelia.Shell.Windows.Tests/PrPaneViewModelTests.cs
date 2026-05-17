using System.Linq;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class PrPaneViewModelTests
{
    private static (PrPaneViewModel Vm, AveliaServices Services) MakeVm()
    {
        var services = Composition.buildStubServices();
        return (new PrPaneViewModel(services), services);
    }

    [Fact]
    public async Task LoadAsync_SeededWorkspace_PopulatesPrHeader()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.True(vm.HasPullRequest);
        Assert.Equal(DesignData.archivePullRequest.Number, vm.Number);
        Assert.Equal(DesignData.archivePullRequest.Title, vm.Title);
        Assert.Equal(DesignData.archivePullRequest.Branch.Value, vm.Branch);
        Assert.Equal(DesignData.archivePullRequest.Base.Value, vm.Base);
        Assert.True(vm.MergeReady);
    }

    [Fact]
    public async Task LoadAsync_SeededWorkspace_TotalsSumAcrossFiles()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var expectedAdd = DesignData.diffFiles.Sum(f => f.Add);
        var expectedDel = DesignData.diffFiles.Sum(f => f.Del);
        Assert.Equal(expectedAdd, vm.TotalAdd);
        Assert.Equal(expectedDel, vm.TotalDel);
        Assert.Equal(DesignData.diffFiles.Count, vm.FileCount);
        Assert.Equal(DesignData.diffFiles.Count, vm.Files.Count);
    }

    [Fact]
    public async Task LoadAsync_SeededWorkspace_ChecksSummaryReflectsPassedCount()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var expectedPassed = DesignData.archivePullRequest.Checks
            .Count(c => c.Status.IsPassed);
        Assert.Equal(DesignData.archivePullRequest.Checks.Length, vm.ChecksTotal);
        Assert.Equal(expectedPassed, vm.ChecksPassed);
        Assert.Equal($"{expectedPassed}/{vm.ChecksTotal} checks", vm.ChecksSummary);
    }

    [Fact]
    public async Task LoadAsync_WorkspaceWithoutPr_ClearsHeaderAndShowsEmptyFileList()
    {
        // trayWorkspace has PrNumber=0 → stub returns Failure NotFound for the PR
        // and an empty file list. The pane should still load cleanly.
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.trayWorkspaceId);

        Assert.False(vm.HasPullRequest);
        Assert.Equal(0, vm.Number);
        Assert.Empty(vm.Files);
        Assert.Equal(0, vm.TotalAdd);
        Assert.Equal(0, vm.TotalDel);
    }

    [Fact]
    public async Task MergeCommand_DisabledWhenNoPr()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.trayWorkspaceId);

        Assert.False(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeCommand_DisabledWhenPrNotMergeReady()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // Toggling MergeReady off should disable the command.
        vm.MergeReady = false;
        Assert.False(vm.MergeCommand.CanExecute(null));

        vm.MergeReady = true;
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeCommand_OnSuccess_MarksMergedAndFlipsMergeReady()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.True(vm.MergeCommand.CanExecute(null));
        await vm.MergeCommand.ExecuteAsync(null);

        Assert.False(vm.MergeReady);
        Assert.True(vm.Status.IsMerged);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task MergeCommand_OnFailure_SurfacesErrorMessage()
    {
        // The stub returns Success on the first merge (PR is merge-ready) and
        // Failure on subsequent attempts (PR is now Merged). Force the second
        // attempt by re-enabling MergeReady locally so CanExecute lets us in.
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);
        await vm.MergeCommand.ExecuteAsync(null);

        Assert.True(vm.Status.IsMerged);
        Assert.False(vm.HasError);

        vm.MergeReady = true;
        await vm.MergeCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public void ActiveTab_DefaultsToChanges()
    {
        var (vm, _) = MakeVm();
        Assert.Equal("Changes", vm.ActiveTab);
    }

    [Fact]
    public async Task FileOpened_OpenCommand_RaisesEventWithPath()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        RelativePath? captured = null;
        vm.FileOpened += (_, path) => captured = path;

        var target = vm.Files.First();
        target.OpenCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(target.Path, captured!.Value);
    }

    [Fact]
    public async Task FileOpened_OpeningARowFocusesThatRowAndClearsOthers()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var second = vm.Files.Skip(1).First();
        second.OpenCommand.Execute(null);

        Assert.True(second.IsFocused);
        Assert.All(vm.Files.Where(f => f != second), f => Assert.False(f.IsFocused));
    }
}

public class DiffFileViewModelTests
{
    [Fact]
    public void Constructor_SplitsPathIntoFolderAndFileName()
    {
        var file = DesignData.diffFiles.First(f =>
            f.Path.Value == "src/ui/components/RepositoryDetailsDialog.tsx");
        var vm = new DiffFileViewModel(file, _ => { });

        Assert.Equal("src/ui/components/", vm.Folder);
        Assert.Equal("RepositoryDetailsDialog.tsx", vm.FileName);
    }

    [Fact]
    public void Constructor_FormatsAddAndDelChips()
    {
        var file = DesignData.diffFiles.First();
        var vm = new DiffFileViewModel(file, _ => { });

        Assert.Equal($"+{file.Add}", vm.AddDisplay);
        Assert.Equal($"-{file.Del}", vm.DelDisplay);
    }

    [Fact]
    public void Constructor_MapsKindToSingleCharacterBadge()
    {
        var modified = DesignData.diffFiles.First(f => f.Kind.IsModified);
        var deleted = DesignData.diffFiles.First(f => f.Kind.IsDeleted);
        var modVm = new DiffFileViewModel(modified, _ => { });
        var delVm = new DiffFileViewModel(deleted, _ => { });

        Assert.Equal("M", modVm.KindBadge);
        Assert.Equal("D", delVm.KindBadge);
    }
}

public class TerminalPanelViewModelTests
{
    [Fact]
    public void Load_BuildsPromptLineFromWorkspaceBaseAndBranch()
    {
        var workspace = DesignData.workspaces.First(w => w.Id.Equals(DesignData.archiveWorkspaceId));
        var vm = new TerminalPanelViewModel();
        vm.Load(workspace);

        Assert.Equal(workspace.Branch.Value, vm.Branch);
        Assert.Equal(workspace.Base.Value, vm.Base);
        Assert.Equal($"→ {workspace.Base.Value} git:({workspace.Branch.Value})", vm.PromptLine);
    }

    [Fact]
    public void ActiveTab_DefaultsToTerminal()
    {
        var vm = new TerminalPanelViewModel();
        Assert.Equal("Terminal", vm.ActiveTab);
        Assert.True(vm.IsTerminalActive);
        Assert.False(vm.IsRunActive);
    }

    [Fact]
    public void RunCommand_FlipsToRunTab()
    {
        var vm = new TerminalPanelViewModel();
        vm.RunCommand.Execute(null);

        Assert.Equal("Run", vm.ActiveTab);
        Assert.True(vm.IsRunActive);
        Assert.False(vm.IsTerminalActive);
    }

    [Fact]
    public void Reset_ClearsBranchBaseAndPrompt()
    {
        var workspace = DesignData.workspaces.First(w => w.Id.Equals(DesignData.archiveWorkspaceId));
        var vm = new TerminalPanelViewModel();
        vm.Load(workspace);

        vm.Reset();

        Assert.Equal(string.Empty, vm.Branch);
        Assert.Equal(string.Empty, vm.Base);
        Assert.Equal(string.Empty, vm.PromptLine);
    }
}
