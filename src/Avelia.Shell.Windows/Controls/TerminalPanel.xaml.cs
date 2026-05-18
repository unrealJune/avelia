using System;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Sticky bottom panel — tab strip + mono prompt line + blinking caret.
/// Real run output (process stdout, exit codes) flows in once
/// <c>IRunService.ObserveOutput</c> is implemented in a later chunk; the
/// XAML here keeps the seat warm with a typed VM binding.
///
/// Storyboard lifecycle: the cursor blink Storyboard is created once when
/// the cursor first loads, started, and stopped on <see cref="OnUnloaded"/>.
/// Without that we'd leak a forever-repeating animation on every page
/// navigation.
/// </summary>
public sealed partial class TerminalPanel : UserControl
{
    private Storyboard? _cursorBlinkStoryboard;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TerminalPanelViewModel),
        typeof(TerminalPanel),
        new PropertyMetadata(null)
    );

    public TerminalPanelViewModel? ViewModel
    {
        get => (TerminalPanelViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnCursorLoaded(object sender, RoutedEventArgs e)
    {
        if (_cursorBlinkStoryboard is not null || sender is not FrameworkElement cursor)
        {
            return;
        }
        var blink = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(530)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(blink, cursor);
        Storyboard.SetTargetProperty(blink, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(blink);
        sb.Begin();
        _cursorBlinkStoryboard = sb;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop and drop the Storyboard so re-loading the control (e.g. after
        // page navigation) doesn't accumulate animations targeting a stale
        // Rectangle. Loaded fires again on re-mount and rebuilds.
        _cursorBlinkStoryboard?.Stop();
        _cursorBlinkStoryboard = null;
    }

    private void OnTabSelectionChanged(
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
        ViewModel.ActiveTab = item.Text;
    }
}
