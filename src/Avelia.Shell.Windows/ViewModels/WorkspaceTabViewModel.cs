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

    [ObservableProperty]
    private string _branch;

    [ObservableProperty]
    private string _base;

    [ObservableProperty]
    private WorkspaceStatus _status;

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
        );
}
