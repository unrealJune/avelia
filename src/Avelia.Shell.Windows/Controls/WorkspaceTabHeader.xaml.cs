using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Header content for a <c>TabViewItem</c> in the title-bar tab strip.
/// Bound to a <see cref="WorkspaceTabViewModel"/>.
/// </summary>
public sealed partial class WorkspaceTabHeader : UserControl
{
    public WorkspaceTabHeader()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(
            nameof(Tab),
            typeof(WorkspaceTabViewModel),
            typeof(WorkspaceTabHeader),
            new PropertyMetadata(null));

    public WorkspaceTabViewModel? Tab
    {
        get => (WorkspaceTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }
}
