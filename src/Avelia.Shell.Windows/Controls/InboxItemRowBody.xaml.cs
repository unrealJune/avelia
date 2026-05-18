using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Body of an inbox row — title / description / time / chevron. The per-kind
/// row templates in <c>InboxPage.xaml</c> own the leading tile (background +
/// glyph) and host this control for the rest of the row.
/// </summary>
public sealed partial class InboxItemRowBody : UserControl
{
    public InboxItemRowBody()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(InboxItemViewModel),
        typeof(InboxItemRowBody),
        new PropertyMetadata(null)
    );

    public InboxItemViewModel? Item
    {
        get => (InboxItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
