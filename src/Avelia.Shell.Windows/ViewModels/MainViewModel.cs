using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Root view-model for <c>MainWindow</c>. Owns: workspace tabs, nav-rail
/// sections + workspace tree, inbox count, theme/rail toggles, and the
/// commands that wire them together.
///
/// Constructed once at shell startup with the typed <see cref="AveliaServices"/>
/// bundle from <see cref="Composition"/>. The shell calls
/// <see cref="InitializeAsync(CancellationToken)"/> once after construction to
/// seed tabs / tree / inbox from the stub services.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AveliaServices _services;

    /// <summary>
    /// Parameterless constructor for design-time / fallback use only.
    /// Builds a fresh stub service graph and delegates to the main ctor.
    /// Production callers pass the shared <see cref="AveliaServices"/> bundle.
    /// </summary>
    public MainViewModel()
        : this(Composition.buildStubServices()) { }

    public MainViewModel(AveliaServices services)
    {
        _services = services;
    }

    // -------- Observable state --------
    //
    // Note: using the field-based [ObservableProperty] pattern rather than
    // partial properties — the MVVM Toolkit 8.4 generator emits the AOT
    // warning (MVVMTK0045) that suggests partial properties, but does not yet
    // *implement* the partial-property feature. Field pattern works today;
    // we'll migrate once the toolkit ships the partial-property generator.

    [ObservableProperty]
    private string _title = "Avelia";

    [ObservableProperty]
    private bool _isRailExpanded = true;

    [ObservableProperty]
    private int _inboxCount;

    [ObservableProperty]
    private WorkspaceTabViewModel? _activeTab;

    [ObservableProperty]
    private NavRailSection _activeSection = NavRailSection.Home;

    public ObservableCollection<WorkspaceTabViewModel> OpenTabs { get; } = new();

    public ObservableCollection<RepoGroupViewModel> RepoGroups { get; } = new();

    /// <summary>
    /// Raised when the user clicks the title-bar "+" button or the rail
    /// "Add repository" item. The host (MainWindow) subscribes to open the
    /// <c>AddRepositoryDialog</c>; the VM stays free of WinUI types so it
    /// link-compiles into the net10.0 test project.
    /// </summary>
    public event EventHandler? OpenAddRepoDialogRequested;

    // -------- Commands --------

    [RelayCommand]
    private void ToggleRail()
    {
        IsRailExpanded = !IsRailExpanded;
    }

    [RelayCommand]
    private void OpenAddRepoDialog()
    {
        OpenAddRepoDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Append a freshly-added repository to the rail tree. Called by the host
    /// after <see cref="AddRepositoryViewModel.RepositoryAdded"/> fires. We
    /// don't re-pull the whole list from the service to avoid losing the
    /// per-group expansion state.
    /// </summary>
    public void AppendRepository(Repository repo)
    {
        // Skip duplicates — the service uses the typed ID and we want the
        // rail to remain idempotent under double-fires (e.g. test harness).
        foreach (var existing in RepoGroups)
        {
            if (existing.Id.Equals(repo.Id))
            {
                return;
            }
        }
        RepoGroups.Add(RepoGroupViewModel.FromRepo(repo));
    }

    [RelayCommand]
    private async Task OpenWorkspace(WorkspaceId id)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Id.Equals(id));
        if (existing is not null)
        {
            ActiveTab = existing;
            ActiveSection = NavRailSection.Home;
            return;
        }

        var result = await _services.Workspaces.GetAsync(id, CancellationToken.None);
        if (!result.IsSuccess)
        {
            return;
        }

        var workspace = result.Value;
        var repoName = await GetRepoNameAsync(workspace.RepoId);
        var tab = WorkspaceTabViewModel.FromWorkspace(workspace, repoName);
        OpenTabs.Add(tab);
        ActiveTab = tab;
        ActiveSection = NavRailSection.Home;
    }

    /// <summary>
    /// Create a new workspace (worktree + branch + conversation) on a repo and
    /// open it as the active tab. The host supplies the branch name from a small
    /// input dialog; validation/creation failures come back in the result for
    /// the host to surface.
    /// </summary>
    public async System.Threading.Tasks.Task<
        OperationResult<global::Avelia.Core.Abstractions.Workspace>
    > CreateWorkspaceAsync(RepositoryId repoId, string branchName)
    {
        var repoResult = await _services.Repositories.GetAsync(repoId, CancellationToken.None);
        if (!repoResult.IsSuccess)
        {
            return OperationResult<global::Avelia.Core.Abstractions.Workspace>.NewFailure(
                repoResult.Error
            );
        }

        var branchParse = BranchName.TryCreate(branchName);
        if (branchParse.IsError)
        {
            return OperationResult<global::Avelia.Core.Abstractions.Workspace>.NewFailure(
                AveliaError.NewValidation(branchParse.ErrorValue)
            );
        }

        var result = await _services.Workspaces.CreateAsync(
            repoId,
            branchParse.ResultValue,
            repoResult.Value.DefaultBase,
            CancellationToken.None
        );

        if (result.IsSuccess)
        {
            var ws = result.Value;
            foreach (var group in RepoGroups)
            {
                if (group.Id.Equals(repoId))
                {
                    group.Workspaces.Add(WorkspaceItemViewModel.FromWorkspace(ws));
                    break;
                }
            }
            await OpenWorkspace(ws.Id);
        }
        return result;
    }

    /// <summary>
    /// Create a workspace with an auto-generated rail-themed branch/worktree
    /// name (no prompt) and open it as the active tab. An empty
    /// <see cref="BranchName"/> sentinel tells the workspace service to pick an
    /// unused name; the Haiku auto-rename later updates only the display title.
    /// Creation failures come back in the result for the host to surface.
    /// </summary>
    public async System.Threading.Tasks.Task<
        OperationResult<global::Avelia.Core.Abstractions.Workspace>
    > CreateWorkspaceAutoAsync(RepositoryId repoId)
    {
        var repoResult = await _services.Repositories.GetAsync(repoId, CancellationToken.None);
        if (!repoResult.IsSuccess)
        {
            return OperationResult<global::Avelia.Core.Abstractions.Workspace>.NewFailure(
                repoResult.Error
            );
        }

        // default(BranchName) is the empty sentinel the workspace service reads
        // as "auto-name from the rail-name pool".
        var result = await _services.Workspaces.CreateAsync(
            repoId,
            default,
            repoResult.Value.DefaultBase,
            CancellationToken.None
        );

        if (result.IsSuccess)
        {
            var ws = result.Value;
            foreach (var group in RepoGroups)
            {
                if (group.Id.Equals(repoId))
                {
                    group.Workspaces.Add(WorkspaceItemViewModel.FromWorkspace(ws));
                    break;
                }
            }
            await OpenWorkspace(ws.Id);
        }
        return result;
    }

    /// <summary>
    /// Delete a workspace (worktree + branch + record), remove it from the rail
    /// tree, and close any open tab. Returns <c>null</c> on success or the error
    /// for the host to surface.
    /// </summary>
    public async Task<AveliaError?> DeleteWorkspaceAsync(WorkspaceId id)
    {
        var result = await _services.Workspaces.DeleteAsync(id, CancellationToken.None);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        foreach (var group in RepoGroups)
        {
            var item = group.Workspaces.FirstOrDefault(w => w.Id.Equals(id));
            if (item is not null)
            {
                group.Workspaces.Remove(item);
                break;
            }
        }

        var tab = OpenTabs.FirstOrDefault(t => t.Id.Equals(id));
        if (tab is not null)
        {
            CloseTab(tab);
        }

        return null;
    }

    [RelayCommand]
    private void CloseTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }
        var idx = OpenTabs.IndexOf(tab);
        if (idx < 0)
        {
            return;
        }
        var wasActive = ReferenceEquals(ActiveTab, tab);
        OpenTabs.RemoveAt(idx);
        if (wasActive)
        {
            ActiveTab = OpenTabs.Count == 0 ? null : OpenTabs[Math.Max(0, idx - 1)];
        }
    }

    /// <summary>
    /// Cycle the active workspace tab. <paramref name="forward"/> moves to the
    /// next tab (Ctrl+Tab); <c>false</c> moves to the previous one
    /// (Ctrl+Shift+Tab). Selection wraps around at either end. No-op when there
    /// are fewer than two open tabs.
    /// </summary>
    [RelayCommand]
    private void CycleTab(bool forward)
    {
        var count = OpenTabs.Count;
        if (count < 2)
        {
            return;
        }

        var idx = ActiveTab is null ? -1 : OpenTabs.IndexOf(ActiveTab);
        if (idx < 0)
        {
            ActiveTab = OpenTabs[0];
            return;
        }

        var next = forward ? (idx + 1) % count : (idx - 1 + count) % count;
        ActiveTab = OpenTabs[next];
    }

    [RelayCommand]
    private void NavigateSection(NavRailSection section)
    {
        ActiveSection = section;
    }

    // -------- Lifecycle --------

    /// <summary>
    /// Load repos, workspaces, and inbox count from the services and populate
    /// the rail tree. Opens the first workspace as the initial active tab.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var reposTask = _services.Repositories.ListAsync(ct);
        var workspacesTask = _services.Workspaces.ListAllAsync(ct);
        var inboxTask = _services.Inbox.ListAsync(ct);

        var repos = await reposTask;
        var workspaces = await workspacesTask;
        var inbox = await inboxTask;

        InboxCount = inbox.Count;

        RepoGroups.Clear();
        var workspacesByRepo = workspaces
            .GroupBy(w => w.RepoId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var repo in repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var group = RepoGroupViewModel.FromRepo(repo);
            if (workspacesByRepo.TryGetValue(repo.Id, out var groupWorkspaces))
            {
                foreach (var w in groupWorkspaces)
                {
                    group.Workspaces.Add(WorkspaceItemViewModel.FromWorkspace(w));
                }
            }
            RepoGroups.Add(group);
        }

        // Seed the initial tab to the first workspace so the shell isn't empty.
        var first = workspaces.FirstOrDefault();
        if (first is not null)
        {
            await OpenWorkspace(first.Id);
        }

        // Drive the rail/tab status dots from live git state (best-effort,
        // off the init path so startup isn't blocked on N git reads).
        _ = RefreshWorkspaceStatusesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reflect the active workspace's live "agent working" state onto its tab +
    /// rail dot. When work starts the dot goes yellow (<c>Working</c>) and a
    /// spinner shows; the yellow dot is sticky and is <em>not</em> cleared when
    /// the turn ends (only the spinner stops) — it persists until the PR merges
    /// (see <see cref="SetWorkspaceMerged"/>).
    /// </summary>
    public void SetWorkspaceAgentWorking(WorkspaceId id, bool working)
    {
        var item = FindRailItem(id);
        if (item is not null)
        {
            item.IsAgentWorking = working;
            if (working)
            {
                item.Status = WorkspaceStatus.Working;
            }
        }

        var tab = OpenTabs.FirstOrDefault(t => t.Id.Equals(id));
        if (tab is not null)
        {
            tab.IsAgentWorking = working;
            if (working)
            {
                tab.Status = WorkspaceStatus.Working;
            }
        }
    }

    /// <summary>
    /// The workspace's pull request merged: drop the sticky yellow "working"
    /// dot and the spinner, settling the dot to <c>Ready</c> (green).
    /// </summary>
    public void SetWorkspaceMerged(WorkspaceId id)
    {
        var item = FindRailItem(id);
        if (item is not null)
        {
            item.IsAgentWorking = false;
            item.Status = WorkspaceStatus.Ready;
        }

        var tab = OpenTabs.FirstOrDefault(t => t.Id.Equals(id));
        if (tab is not null)
        {
            tab.IsAgentWorking = false;
            tab.Status = WorkspaceStatus.Ready;
        }
    }

    private WorkspaceItemViewModel? FindRailItem(WorkspaceId id)
    {
        foreach (var group in RepoGroups)
        {
            foreach (var item in group.Workspaces)
            {
                if (item.Id.Equals(id))
                {
                    return item;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Map a live <see cref="WorktreeStatus"/> to the status-dot vocabulary:
    /// conflicted files → Conflict; any uncommitted change → Active; clean but
    /// ahead of upstream → Ready; otherwise the workspace's stored status.
    /// </summary>
    private static WorkspaceStatus DisplayStatus(WorktreeStatus status, WorkspaceStatus fallback)
    {
        foreach (var file in status.Files)
        {
            if (file.IsConflicted)
            {
                return WorkspaceStatus.Conflict;
            }
        }
        if (status.HasUncommittedChanges)
        {
            return WorkspaceStatus.Active;
        }
        if (status.AheadBehind.Ahead > 0)
        {
            return WorkspaceStatus.Ready;
        }
        return fallback;
    }

    /// <summary>
    /// Refresh every workspace's rail-item (and open-tab) status dot from a live
    /// <c>git status</c> read. Best-effort; a failure leaves the stored status.
    /// </summary>
    public async Task RefreshWorkspaceStatusesAsync(CancellationToken ct)
    {
        foreach (var group in RepoGroups)
        {
            foreach (var item in group.Workspaces.ToList())
            {
                // A workspace showing the sticky "working" dot (agent ran, not
                // yet merged) keeps it until the merge lands — the git sweep
                // must not downgrade it back to a diff-derived colour.
                if (item.Status.IsWorking)
                {
                    continue;
                }
                try
                {
                    var result = await _services
                        .Workspaces.GetStatusAsync(item.Id, ct)
                        .ConfigureAwait(true);
                    if (!result.IsSuccess)
                    {
                        continue;
                    }
                    var display = DisplayStatus(result.Value, item.Status);
                    item.Status = display;
                    var tab = OpenTabs.FirstOrDefault(t => t.Id.Equals(item.Id));
                    if (tab is not null)
                    {
                        tab.Status = display;
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainViewModel] status refresh failed for {item.Id}: {ex.Message}"
                    );
                }
            }
        }
    }

    // -------- Helpers --------

    private async System.Threading.Tasks.Task<string> GetRepoNameAsync(RepositoryId repoId)
    {
        var result = await _services.Repositories.GetAsync(repoId, CancellationToken.None);
        return result.IsSuccess ? result.Value.Name : "";
    }
}
