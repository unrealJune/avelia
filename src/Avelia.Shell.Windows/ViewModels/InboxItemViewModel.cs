using System;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One row in the inbox list. Pre-projects the typed <see cref="InboxItem"/>
/// snapshot onto string + enum properties so the row template can bind via
/// <c>x:Bind</c> without converters.
///
/// Holds no observable state: every property is set once at construction.
/// The row's open affordance routes through <see cref="InboxViewModel.OpenCommand"/>
/// (parent-owned) rather than per-row, so a single <c>WorkspaceOpenRequested</c>
/// publisher exists.
/// </summary>
public partial class InboxItemViewModel : ObservableObject
{
    public InboxItemViewModel(InboxItem item)
    {
        Id = item.Id;
        Title = item.Title;
        Description = item.Description;
        TimeAgo = item.TimeAgo;
        Kind = item.Kind;
        LinkedWorkspaceId = item.LinkedWorkspaceId;
        HasLinkedWorkspace = !item.LinkedWorkspaceId.Equals(EmptyWorkspaceId);
    }

    /// <summary>
    /// Sentinel that <see cref="InboxItem.LinkedWorkspaceId"/> equals when the
    /// item has no workspace association. <c>DomainTypes.fs:351</c> documents
    /// <c>Guid.Empty</c> as the "unset" convention; the F# single-case DU
    /// constructor wraps it identically from C#.
    /// </summary>
    private static readonly WorkspaceId EmptyWorkspaceId = WorkspaceId.NewWorkspaceId(Guid.Empty);

    public Guid Id { get; }

    public string Title { get; }

    public string Description { get; }

    public string TimeAgo { get; }

    public InboxItemKind Kind { get; }

    public WorkspaceId LinkedWorkspaceId { get; }

    /// <summary>
    /// True when <see cref="LinkedWorkspaceId"/> is not the sentinel
    /// <c>Guid.Empty</c>. The page uses this to gate the open affordance — items
    /// without a linked workspace render the chevron disabled.
    /// </summary>
    public bool HasLinkedWorkspace { get; }
}
