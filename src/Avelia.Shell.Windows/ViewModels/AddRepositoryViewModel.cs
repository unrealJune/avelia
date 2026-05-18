using System;
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
/// View-model for <c>AddRepositoryDialog</c>. Carries the state for all three
/// tabs (Local folder / Git URL / Clone from GitHub) so the dialog can toggle
/// between them without rebuilding child controls, and exposes one
/// <see cref="AddCommand"/> the dialog primary button maps to. The command
/// dispatches to the right kind based on <see cref="ActiveTab"/>.
///
/// Keeps the WinUI types out of the type signature so the VM compiles into the
/// net10.0 test project — same constraint that drives <c>IUiDispatcher</c>.
/// Result of <see cref="AddCommand"/> is published via the
/// <see cref="RepositoryAdded"/> event; the dialog uses that to close itself
/// and surface the new repo to the shell.
/// </summary>
public partial class AddRepositoryViewModel : ObservableObject
{
    private readonly IRepositoryService _repositories;

    public AddRepositoryViewModel(AveliaServices services)
    {
        _repositories = services.Repositories;
    }

    /// <summary>
    /// Three modes the dialog exposes. The active mode drives which inputs
    /// the dialog renders and which branch <see cref="AddCommand"/> takes.
    /// </summary>
    public enum Tab
    {
        LocalFolder,
        GitUrl,
        GitHub,
    }

    /// <summary>
    /// Fired after a successful <see cref="AddCommand"/>. The dialog
    /// subscribes to close itself; the shell subscribes to refresh the rail
    /// tree. Carries the newly-added repo so callers can highlight it.
    /// </summary>
    public event EventHandler<Repository>? RepositoryAdded;

    // -------- Tab + recent-repos --------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private Tab _activeTab = Tab.LocalFolder;

    /// <summary>
    /// Recent repositories — populated from <see cref="IRepositoryService.ListAsync"/>
    /// during <see cref="LoadAsync"/>. Clicking a row fills
    /// <see cref="LocalPath"/> so the user can re-add it with one click. The
    /// stub seeds eight; a real-backend implementation will likely surface
    /// the last-opened ones with timestamps.
    /// </summary>
    public ObservableCollection<RecentRepoItem> RecentRepos { get; } = new();

    // -------- Local folder tab --------

    /// <summary>Absolute path the user typed or picked via Browse.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _localPath = string.Empty;

    /// <summary>
    /// Validation message for the local path, or empty when input is valid /
    /// empty. Surfaces the same <see cref="RepoPath.TryCreate"/> rejections
    /// (traversal, whitespace) the F# core enforces. Recomputed inside
    /// <see cref="OnLocalPathChanged"/> so the bound text + visibility stay in
    /// lockstep without two passes through the F# validator per keystroke.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalPathError))]
    private string _localPathError = string.Empty;

    /// <summary>True when <see cref="LocalPathError"/> has content. Drives the
    /// validation row's visibility in the dialog.</summary>
    public bool HasLocalPathError => !string.IsNullOrEmpty(LocalPathError);

    partial void OnLocalPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            LocalPathError = string.Empty;
            return;
        }
        var result = RepoPath.TryCreate(value);
        LocalPathError = result.IsError ? result.ErrorValue : string.Empty;
    }

    // -------- Git URL tab --------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsSshUrl))]
    private string _gitUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _cloneToPath = string.Empty;

    /// <summary>
    /// Lights up the "SSH detected — make sure ssh-agent is running" InfoBar
    /// in the Git URL tab. Matches the design's hint band when the user
    /// pastes a <c>git@</c> URL.
    /// </summary>
    public bool IsSshUrl =>
        !string.IsNullOrWhiteSpace(GitUrl)
        && (
            GitUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || GitUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
        );

    // -------- GitHub tab --------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _gitHubQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private GitHubRepoItem? _selectedGitHubRepo;

    /// <summary>
    /// Placeholder list — the GitHub search backend lands with the VCS
    /// adapter in Chunk 10. Until then the tab renders an empty state hint.
    /// </summary>
    public ObservableCollection<GitHubRepoItem> GitHubResults { get; } = new();

    // -------- Error surface --------

    /// <summary>
    /// Last add-failure reason. Cleared on every new <see cref="AddAsync"/>
    /// attempt and on tab switch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Tab switch clears the error banner — it's specific to the previous
    // attempt, not the new mode. CanExecute is re-queried via the
    // field-level `[NotifyCanExecuteChangedFor]`.
    partial void OnActiveTabChanged(Tab value) => ErrorMessage = string.Empty;

    // -------- Commands --------

    private bool CanAdd() =>
        ActiveTab switch
        {
            Tab.LocalFolder => !string.IsNullOrWhiteSpace(LocalPath)
                && string.IsNullOrEmpty(LocalPathError),
            Tab.GitUrl => !string.IsNullOrWhiteSpace(GitUrl)
                && !string.IsNullOrWhiteSpace(CloneToPath),
            Tab.GitHub => SelectedGitHubRepo is not null,
            _ => false,
        };

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        ErrorMessage = string.Empty;
        switch (ActiveTab)
        {
            case Tab.LocalFolder:
                await AddLocalAsync().ConfigureAwait(true);
                break;
            case Tab.GitUrl:
                // The Git URL path runs through the same AddAsync as a local
                // folder (the clone-to target is the local path). Cloning
                // itself lands with the VCS adapter in Chunk 10 — for now
                // we register the destination and trust the user has cloned
                // it independently.
                await AddClonedAsync().ConfigureAwait(true);
                break;
            case Tab.GitHub:
                // GitHub search/clone is gated on the VCS adapter (Chunk 10).
                // Disabled at the button level; if we get here it's a no-op.
                ErrorMessage = "GitHub clone needs the VCS adapter (Chunk 10).";
                break;
        }
    }

    private async Task AddLocalAsync()
    {
        var pathResult = RepoPath.TryCreate(LocalPath);
        if (pathResult.IsError)
        {
            ErrorMessage = pathResult.ErrorValue;
            return;
        }

        var defaultBase = BranchName.Create("main");
        var result = await _repositories
            .AddAsync(pathResult.ResultValue, defaultBase, CancellationToken.None)
            .ConfigureAwait(true);
        HandleAddResult(result);
    }

    private async Task AddClonedAsync()
    {
        var pathResult = RepoPath.TryCreate(CloneToPath);
        if (pathResult.IsError)
        {
            ErrorMessage = pathResult.ErrorValue;
            return;
        }
        var defaultBase = BranchName.Create("main");
        var result = await _repositories
            .AddAsync(pathResult.ResultValue, defaultBase, CancellationToken.None)
            .ConfigureAwait(true);
        HandleAddResult(result);
    }

    private void HandleAddResult(OperationResult<Repository> result)
    {
        if (result.IsSuccess)
        {
            RepositoryAdded?.Invoke(this, result.Value);
            return;
        }
        ErrorMessage = result.Error.Match(
            onNotFound: r => $"Not found: {r}",
            onValidation: m => m,
            onUnauthorized: () => "Not authorized.",
            onConflict: m => m,
            onNetwork: m => $"Network error: {m}",
            onInternal: m => $"Internal error: {m}"
        );
    }

    // -------- Lifecycle --------

    /// <summary>
    /// Hydrate the recent-repos list. Cheap on the stub; real persistence
    /// (Chunk 10) will sort by last-opened.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var repos = await _repositories.ListAsync(ct).ConfigureAwait(true);
        RecentRepos.Clear();
        foreach (
            var repo in repos
                .OrderByDescending(r => r.IsOpen)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        )
        {
            RecentRepos.Add(new RecentRepoItem(repo));
        }
    }

    /// <summary>
    /// User picked a recent repo — fill the local path so they can re-add it
    /// without retyping. Switches to the Local Folder tab so the input is
    /// visible.
    /// </summary>
    public void PickRecent(RecentRepoItem item)
    {
        ActiveTab = Tab.LocalFolder;
        LocalPath = item.Path;
    }
}

/// <summary>
/// One row in the recent-repos list under the Local Folder tab. Carries the
/// repo's display name, full path, and a workspace-count chip the design
/// renders on the right.
/// </summary>
public sealed class RecentRepoItem
{
    public RecentRepoItem(Repository repo)
    {
        Name = repo.Name;
        Path = repo.Path.Value;
        // The stub doesn't track per-repo workspace counts on the Repository
        // record; the rail tree derives them. Until that's threaded through,
        // the row uses an empty chip — visually inert but the slot is wired.
        WorkspaceCount = string.Empty;
    }

    public string Name { get; }
    public string Path { get; }

    /// <summary>Right-aligned workspace-count chip text. Empty hides the chip.</summary>
    public string WorkspaceCount { get; }

    /// <summary>Drives the count-chip's <c>Visibility</c> binding declaratively.</summary>
    public bool HasCount => !string.IsNullOrEmpty(WorkspaceCount);
}

/// <summary>
/// One result in the GitHub-clone tab. Backed by a real GitHub-search call
/// once the VCS adapter lands (Chunk 10). Until then the list is empty so
/// <see cref="AddRepositoryViewModel.AddCommand"/> stays disabled on that tab.
/// </summary>
public sealed class GitHubRepoItem
{
    public GitHubRepoItem(string owner, string name, string description)
    {
        Owner = owner;
        Name = name;
        Description = description;
    }

    public string Owner { get; }
    public string Name { get; }
    public string Description { get; }
    public string FullName => $"{Owner}/{Name}";
}
