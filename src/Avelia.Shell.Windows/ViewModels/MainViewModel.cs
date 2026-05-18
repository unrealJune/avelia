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

    // -------- Commands --------

    [RelayCommand]
    private void ToggleRail()
    {
        IsRailExpanded = !IsRailExpanded;
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
    }

    // -------- Helpers --------

    private async Task<string> GetRepoNameAsync(RepositoryId repoId)
    {
        var result = await _services.Repositories.GetAsync(repoId, CancellationToken.None);
        return result.IsSuccess ? result.Value.Name : "";
    }
}
