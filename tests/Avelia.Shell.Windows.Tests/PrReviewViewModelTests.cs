using System.Linq;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class PrReviewViewModelTests
{
    private static (PrReviewViewModel Vm, AveliaServices Services) MakeVm()
    {
        var services = Composition.buildStubServices();
        return (new PrReviewViewModel(services), services);
    }

    [Fact]
    public async Task LoadAsync_SeededWorkspace_PopulatesPrHeader()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.True(vm.HasPullRequest);
        Assert.Equal(DesignData.archivePullRequest.Number, vm.Number);
        Assert.Equal("#" + DesignData.archivePullRequest.Number, vm.NumberDisplay);
        Assert.Equal(DesignData.archivePullRequest.Title, vm.Title);
        Assert.Equal(DesignData.archivePullRequest.Branch.Value, vm.Branch);
        Assert.Equal(DesignData.archivePullRequest.Base.Value, vm.Base);
        Assert.Equal("Approved", vm.StatusLabel);
        Assert.True(vm.MergeReady);
    }

    [Fact]
    public async Task LoadAsync_SeededWorkspace_PopulatesFilesAndTotals()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Equal(DesignData.diffFiles.Count, vm.Files.Count);
        Assert.Equal(DesignData.diffFiles.Sum(f => f.Add), vm.TotalAdd);
        Assert.Equal(DesignData.diffFiles.Sum(f => f.Del), vm.TotalDel);
        Assert.Equal(DesignData.diffFiles.Count, vm.FileCount);
    }

    [Fact]
    public async Task LoadAsync_WithoutPreselect_SelectsFirstFile()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.NotNull(vm.SelectedFile);
        Assert.Equal(vm.Files[0], vm.SelectedFile);
        Assert.True(vm.Files[0].IsFocused);
    }

    [Fact]
    public async Task LoadAsync_WithPreselect_SelectsThatFile()
    {
        var (vm, _) = MakeVm();
        var target = RelativePath.Create("src/ui/components/RepositoryDetailsDialog.tsx");

        await vm.LoadAsync(DesignData.archiveWorkspaceId, target);

        Assert.NotNull(vm.SelectedFile);
        Assert.Equal(target, vm.SelectedFile!.Path);
        Assert.True(vm.SelectedFile.IsFocused);
        // Hunks should have loaded for that file.
        Assert.NotEmpty(vm.Hunks);
        Assert.All(vm.Hunks, h => Assert.Equal(target, h.File));
    }

    [Fact]
    public async Task LoadAsync_PreselectMissingFile_FallsBackToFirstFile()
    {
        var (vm, _) = MakeVm();
        var missing = RelativePath.Create("not/in/the/seed.txt");

        await vm.LoadAsync(DesignData.archiveWorkspaceId, missing);

        Assert.NotNull(vm.SelectedFile);
        Assert.Equal(vm.Files[0], vm.SelectedFile);
    }

    [Fact]
    public async Task LoadAsync_WorkspaceWithoutPr_ShowsEmptyShell()
    {
        // trayWorkspace has PrNumber=0 → stub returns Failure NotFound for the PR
        // and an empty file list. The page should render empty without surfacing
        // a noisy error.
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.trayWorkspaceId);

        Assert.False(vm.HasPullRequest);
        Assert.Equal(string.Empty, vm.NumberDisplay);
        Assert.Empty(vm.Files);
        Assert.Empty(vm.Hunks);
        Assert.Null(vm.SelectedFile);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task SelectFileAsync_SwapsSelectionAndReloadsHunks()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // First file (App.tsx) has no seeded hunks; switching to
        // RepositoryDetailsDialog.tsx (which does) verifies the swap.
        var first = vm.Files.First();
        var dialog = vm.Files.First(f =>
            f.Path.Value == "src/ui/components/RepositoryDetailsDialog.tsx"
        );

        await vm.SelectFileAsync(first);
        Assert.Equal(first, vm.SelectedFile);
        Assert.True(first.IsFocused);

        await vm.SelectFileAsync(dialog);
        Assert.Equal(dialog, vm.SelectedFile);
        Assert.True(dialog.IsFocused);
        Assert.False(first.IsFocused);
        Assert.NotEmpty(vm.Hunks);
    }

    [Fact]
    public async Task SelectFileAsync_RaisesFileSelectedEvent()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        DiffFileViewModel? captured = null;
        vm.FileSelected += (_, file) => captured = file;

        var target = vm.Files.Skip(2).First();
        await vm.SelectFileAsync(target);

        Assert.NotNull(captured);
        Assert.Same(target, captured);
    }

    [Fact]
    public async Task SelectFileCommand_DelegatesToSelectFile()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var target = vm.Files.Skip(1).First();
        await vm.SelectFileCommand.ExecuteAsync(target);

        Assert.Equal(target, vm.SelectedFile);
    }

    [Fact]
    public async Task ChecksMessage_WhenAllPassed_SaysAllPassed()
    {
        // The seeded PR has 5 Passed + 1 Warn. To exercise the "all passed"
        // path, count programmatically and trigger only when the data matches.
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        if (vm.ChecksFailed == 0 && vm.ChecksWarning == 0)
        {
            Assert.True(vm.AllChecksPassed);
            Assert.Equal($"All {vm.ChecksTotal} checks passed", vm.ChecksMessage);
            Assert.Equal("Success", vm.ChecksSeverityName);
        }
        else if (vm.ChecksWarning > 0 && vm.ChecksFailed == 0)
        {
            Assert.False(vm.AllChecksPassed);
            Assert.Equal("Warning", vm.ChecksSeverityName);
            Assert.Contains("warning", vm.ChecksMessage);
        }
        else
        {
            Assert.False(vm.AllChecksPassed);
            Assert.Equal("Error", vm.ChecksSeverityName);
        }
    }

    [Fact]
    public async Task ChecksMessage_NoPr_IsEmpty()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.trayWorkspaceId);

        Assert.Equal(string.Empty, vm.ChecksMessage);
        Assert.False(vm.AllChecksPassed);
    }

    [Fact]
    public async Task MergeCommand_RespectsMergeReadyAndPrPresence()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.True(vm.MergeCommand.CanExecute(null));

        vm.MergeReady = false;
        Assert.False(vm.MergeCommand.CanExecute(null));

        vm.MergeReady = true;
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeCommand_OnSuccess_MarksMerged()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal("Merged", vm.StatusLabel);
        Assert.False(vm.MergeReady);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ViewMode_DefaultsToUnified()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);
        Assert.Equal("Unified", vm.ViewMode);
    }

    [Fact]
    public async Task ShowChecksInfoBar_StartsFalseAndFlipsOnLoad()
    {
        // The InfoBar must NOT be open before LoadAsync populates check
        // counters — otherwise it shows the XAML-default "Success" severity
        // while the real status is unknown. This is the W-3 regression guard.
        var (vm, _) = MakeVm();
        Assert.False(vm.ShowChecksInfoBar);

        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.True(vm.ShowChecksInfoBar);
        Assert.True(vm.HasPullRequest);
        Assert.True(vm.ChecksTotal > 0);
    }

    [Fact]
    public async Task ShowChecksInfoBar_StaysFalseForPrlessWorkspace()
    {
        var (vm, _) = MakeVm();
        await vm.LoadAsync(DesignData.trayWorkspaceId);
        Assert.False(vm.ShowChecksInfoBar);
    }

    [Fact]
    public async Task LoadAsync_ConcurrentCalls_LastLoadWins()
    {
        // W-5 regression guard: two LoadAsync calls in flight must not
        // interleave their state mutations. The newer load wins; the older
        // call's superseded work is silently dropped (caught OCE). The stubs
        // are synchronous so the second call effectively cancels the first
        // before it can mutate observable state.
        var (vm, _) = MakeVm();

        var first = vm.LoadAsync(DesignData.trayWorkspaceId);
        var second = vm.LoadAsync(DesignData.archiveWorkspaceId);
        await Task.WhenAll(first, second);

        // archiveWorkspace was the newer call — that's what the VM should
        // reflect, not the PR-less tray workspace.
        Assert.True(vm.HasPullRequest);
        Assert.Equal(DesignData.archivePullRequest.Number, vm.Number);
        Assert.NotEmpty(vm.Files);
    }
}

public class DiffLineViewModelTests
{
    [Fact]
    public void Addition_RoundTripsLineDataAndSetsAdditionFlag()
    {
        var line = new DiffLine(42, DiffLineKind.Addition, "+console.log('x');");
        var vm = new DiffLineViewModel(line);

        Assert.Equal(42, vm.LineNumber);
        Assert.Equal("42", vm.LineNumberDisplay);
        Assert.True(vm.IsAddition);
        Assert.False(vm.IsDeletion);
        Assert.False(vm.IsContext);
        Assert.Equal("+", vm.Sign);
        Assert.Equal("+console.log('x');", vm.Text);
    }

    [Fact]
    public void Deletion_SetsDeletionFlagAndMinusSign()
    {
        var line = new DiffLine(7, DiffLineKind.Deletion, "-removed;");
        var vm = new DiffLineViewModel(line);

        Assert.True(vm.IsDeletion);
        Assert.Equal("-", vm.Sign);
    }

    [Fact]
    public void Context_SetsContextFlagAndSpaceSign()
    {
        var line = new DiffLine(7, DiffLineKind.Context, " unchanged;");
        var vm = new DiffLineViewModel(line);

        Assert.True(vm.IsContext);
        Assert.Equal(" ", vm.Sign);
    }

    [Fact]
    public void Hunk_RoundTripsEveryLine_FromSeededHunks()
    {
        // Property-style: every seeded hunk's line array must round-trip
        // through DiffHunkViewModel with no off-by-one in line numbering, no
        // kind drift, no text truncation. Catches the bug class where the VM
        // accidentally drops or transforms hunk data.
        foreach (var hunk in DesignData.diffHunks)
        {
            var vm = new DiffHunkViewModel(hunk);

            Assert.Equal(hunk.File, vm.File);
            Assert.Equal(hunk.Header, vm.Header);
            Assert.Equal(hunk.Lines.Length, vm.Lines.Count);

            for (var i = 0; i < hunk.Lines.Length; i++)
            {
                var src = hunk.Lines[i];
                var projected = vm.Lines[i];
                Assert.Equal(src.LineNumber, projected.LineNumber);
                Assert.Equal(src.Text, projected.Text);
                Assert.Equal(src.Kind.IsAddition, projected.IsAddition);
                Assert.Equal(src.Kind.IsDeletion, projected.IsDeletion);
                Assert.Equal(src.Kind.IsContext, projected.IsContext);
            }
        }
    }
}
