using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Single-row presentation of a <see cref="RecentRepoItem"/>. The XAML binds
/// directly to the <see cref="Item"/> DP via <c>x:Bind</c> — no code-behind
/// projection is needed. Mirrors the declarative shape of <c>WorkspaceTreeItem</c>.
/// </summary>
public sealed partial class RecentRepoRow : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(RecentRepoItem),
        typeof(RecentRepoRow),
        new PropertyMetadata(null)
    );

    public RecentRepoRow()
    {
        InitializeComponent();
    }

    public RecentRepoItem? Item
    {
        get => (RecentRepoItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
