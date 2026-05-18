using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Header row of the workspace right pane — PR number, title, branch/base,
/// stats, and the Merge button. Backed by <see cref="PrPaneViewModel"/>; the
/// Merge button collapses when there's no PR for the workspace.
/// </summary>
public sealed partial class PrHeader : UserControl
{
    public PrHeader()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(PrPaneViewModel),
        typeof(PrHeader),
        new PropertyMetadata(null)
    );

    public PrPaneViewModel? ViewModel
    {
        get => (PrPaneViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
