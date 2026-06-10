using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One workspace entry inside a <see cref="RepoGroupViewModel"/> in the nav
/// rail's workspace tree (expanded mode). Distinct from
/// <see cref="WorkspaceTabViewModel"/> which represents an *open tab* — many
/// workspaces sit in the rail; only the open ones appear as tabs.
/// </summary>
public partial class WorkspaceItemViewModel : ObservableObject
{
    public WorkspaceItemViewModel(
        WorkspaceId id,
        string branch,
        WorkspaceStatus status,
        int add,
        int del
    )
    {
        Id = id;
        _branch = branch;
        _displayName = branch;
        _status = status;
        _add = add;
        _del = del;
    }

    public WorkspaceId Id { get; }

    [ObservableProperty]
    private string _branch;

    /// <summary>
    /// Label shown in the nav rail. Defaults to the (auto-generated) branch
    /// name and is replaced by the conversation's auto-renamed title once the
    /// Haiku summariser produces one. Kept in sync by <c>MainViewModel</c>'s
    /// per-workspace title watcher.
    /// </summary>
    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private WorkspaceStatus _status;

    [ObservableProperty]
    private int _add;

    [ObservableProperty]
    private int _del;

    [ObservableProperty]
    private bool _isActive;

    public static WorkspaceItemViewModel FromWorkspace(Workspace w) =>
        new(id: w.Id, branch: w.Branch.Value, status: w.Status, add: w.DiffAdd, del: w.DiffDel);
}
