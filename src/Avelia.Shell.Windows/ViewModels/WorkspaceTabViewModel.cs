using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One entry in the title-bar TabView — represents an open workspace.
/// Mirrors the design's tab where the active tab carries <c>branch · base</c>
/// and a status dot.
/// </summary>
public partial class WorkspaceTabViewModel : ObservableObject
{
    public WorkspaceTabViewModel(
        WorkspaceId id,
        string branch,
        string baseBranch,
        WorkspaceStatus status,
        int add,
        int del,
        string repoName
    )
    {
        Id = id;
        _branch = branch;
        _base = baseBranch;
        _status = status;
        _add = add;
        _del = del;
        _repoName = repoName;
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

    /// <summary>What the tab shows: the title when set, else the branch name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Branch : Title;

    [ObservableProperty]
    private string _base;

    [ObservableProperty]
    private WorkspaceStatus _status;

    /// <summary>
    /// The workspace's agent is mid-run. Drives a small spinner next to the
    /// tab's status dot; set/cleared by <see cref="MainViewModel"/> from the
    /// active workspace's live conversation stream.
    /// </summary>
    [ObservableProperty]
    private bool _isAgentWorking;

    [ObservableProperty]
    private int _add;

    [ObservableProperty]
    private int _del;

    [ObservableProperty]
    private string _repoName;

    public static WorkspaceTabViewModel FromWorkspace(Workspace w, string repoName) =>
        new(
            id: w.Id,
            branch: w.Branch.Value,
            baseBranch: w.Base.Value,
            status: w.Status,
            add: w.DiffAdd,
            del: w.DiffDel,
            repoName: repoName
        )
        {
            Title = w.Title,
        };
}
