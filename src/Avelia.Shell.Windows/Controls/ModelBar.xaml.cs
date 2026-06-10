using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Unified model bar — model · reasoning effort · context tier in one pill.
/// Pure presentation: all state (option lists + selections + change callbacks)
/// lives in the bound <see cref="ModelBarViewModel"/>, shared by the composer
/// and the Settings → Agents subpage.
/// </summary>
public sealed partial class ModelBar : UserControl
{
    public ModelBar()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(ModelBarViewModel),
        typeof(ModelBar),
        new PropertyMetadata(null, OnViewModelChanged)
    );

    public ModelBarViewModel? ViewModel
    {
        get => (ModelBarViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ModelBar)d).Bindings.Update();
    }
}
