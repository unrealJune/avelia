using Avelia.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Small colored ellipse rendering a <see cref="WorkspaceStatus"/>. Used by
/// the tab strip and the nav-rail workspace tree.
/// </summary>
public sealed partial class StatusDot : UserControl
{
    public StatusDot()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(WorkspaceStatus),
            typeof(StatusDot),
            new PropertyMetadata(WorkspaceStatus.Draft));

    public WorkspaceStatus Status
    {
        get => (WorkspaceStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
