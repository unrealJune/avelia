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
        _status = status;
        _add = add;
        _del = del;
    }

    public WorkspaceId Id { get; }

    /// <summary>
    /// Human display title (e.g. "Add MCP Server"), set via the rename flow.
    /// Empty falls back to <see cref="Branch"/> in <see cref="DisplayName"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _branch;

    /// <summary>What the rail row shows: the title when set, else the branch name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Branch : Title;

    [ObservableProperty]
    private WorkspaceStatus _status;

    /// <summary>
    /// The workspace's agent is mid-run. Drives a pulse on the rail item's
    /// status dot.
    /// </summary>
    [ObservableProperty]
    private bool _isAgentWorking;

    [ObservableProperty]
    private int _add;

    [ObservableProperty]
    private int _del;

    [ObservableProperty]
    private bool _isActive;

    public static WorkspaceItemViewModel FromWorkspace(Workspace w) =>
        new(id: w.Id, branch: w.Branch.Value, status: w.Status, add: w.DiffAdd, del: w.DiffDel)
        {
            Title = w.Title,
        };
}
