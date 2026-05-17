using System.Collections.ObjectModel;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One repository group in the nav rail tree (expanded mode). Hosts a chevron
/// + name + workspace count; expanding reveals the workspace items.
/// </summary>
public partial class RepoGroupViewModel : ObservableObject
{
    public RepoGroupViewModel(RepositoryId id, string name, bool isExpanded)
    {
        Id = id;
        _name = name;
        _isExpanded = isExpanded;
    }

    public RepositoryId Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = new();

    public int WorkspaceCount => Workspaces.Count;

    public static RepoGroupViewModel FromRepo(Repository r) =>
        new(id: r.Id, name: r.Name, isExpanded: r.IsOpen);
}
