using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Content of a NavigationViewItem inside an expanded repository group.
/// Bound to a <see cref="WorkspaceItemViewModel"/>.
/// </summary>
public sealed partial class WorkspaceTreeItem : UserControl
{
    public WorkspaceTreeItem()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(WorkspaceItemViewModel),
            typeof(WorkspaceTreeItem),
            new PropertyMetadata(null));

    public WorkspaceItemViewModel? Item
    {
        get => (WorkspaceItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
