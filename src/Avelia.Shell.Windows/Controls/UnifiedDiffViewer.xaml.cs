using System.ComponentModel;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Right-pane diff viewer for <c>PrReviewPage</c>. Header card (file path +
/// stats + view-mode pivot + view-file action), checks InfoBar, then a
/// virtualizing ListView of hunks (each rendering its own header bar + lines).
///
/// Severity for the InfoBar is computed in the VM as a string name and
/// mirrored onto the InfoBar.Severity DP here — x:Bind can't convert from the
/// VM's clean string state into the WinUI <c>InfoBarSeverity</c> enum without
/// a converter, and keeping the VM free of <c>Microsoft.UI.Xaml.Controls</c>
/// references lets it stay link-compileable into the net10.0 test project.
/// </summary>
public sealed partial class UnifiedDiffViewer : UserControl
{
    public UnifiedDiffViewer()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(PrReviewViewModel),
        typeof(UnifiedDiffViewer),
        new PropertyMetadata(null, OnViewModelChanged)
    );

    public PrReviewViewModel? ViewModel
    {
        get => (PrReviewViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UnifiedDiffViewer self)
        {
            return;
        }
        if (e.OldValue is PrReviewViewModel old)
        {
            old.PropertyChanged -= self.OnVmPropertyChanged;
        }
        if (e.NewValue is PrReviewViewModel @new)
        {
            @new.PropertyChanged += self.OnVmPropertyChanged;
            self.ApplyChecksSeverity(@new);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(PrReviewViewModel.ChecksSeverityName)
            && sender is PrReviewViewModel vm
        )
        {
            ApplyChecksSeverity(vm);
        }
    }

    private void ApplyChecksSeverity(PrReviewViewModel vm)
    {
        ChecksInfoBar.Severity = vm.ChecksSeverityName switch
        {
            "Error" => InfoBarSeverity.Error,
            "Warning" => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Success,
        };
    }

    private void OnViewModeSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args
    )
    {
        if (
            ViewModel is null
            || sender.SelectedItem is not SelectorBarItem item
            || item.Text is null
        )
        {
            return;
        }
        // Defence in depth: Split is IsEnabled=False, but if a future XAML
        // change accidentally enables it, the unimplemented view shouldn't
        // silently activate — bounce selection back to Unified.
        if (!item.IsEnabled)
        {
            sender.SelectedItem = sender.Items[0];
            return;
        }
        ViewModel.ViewMode = item.Text;
    }
}
