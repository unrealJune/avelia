using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Composer toolbar badge showing the workspace's active model — dot + name +
/// caret. Functional dropdown wiring lands with the Settings page (Chunk 5).
/// </summary>
public sealed partial class ModelBadge : UserControl
{
    public ModelBadge()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ModelNameProperty =
        DependencyProperty.Register(
            nameof(ModelName),
            typeof(string),
            typeof(ModelBadge),
            new PropertyMetadata("Sonnet 4.5"));

    public string ModelName
    {
        get => (string)GetValue(ModelNameProperty);
        set => SetValue(ModelNameProperty, value);
    }
}
