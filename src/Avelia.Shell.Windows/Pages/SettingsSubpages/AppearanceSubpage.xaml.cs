using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Pages.SettingsSubpages;

/// <summary>
/// Appearance subpage — theme + accent + density + transparency + open-with-right-panel.
/// Two-way toggles bind directly to the VM. The Theme and Density SelectorBars
/// can't two-way-bind to an enum (the control exposes <c>SelectedItem</c> as a
/// <c>SelectorBarItem</c>, not a tag), so this code-behind translates between
/// the bar's items and the VM properties. The accent swatches and side-nav
/// active bar are fully bound via <c>x:Bind</c> through observable wrapper
/// items — no visual-tree walks here.
/// </summary>
public sealed partial class AppearanceSubpage : UserControl
{
    private AppearanceSubpageViewModel? _viewModel;
    private bool _suppressSelectionEvents;

    public AppearanceSubpage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public AppearanceSubpageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
            Bindings.Update();
            SyncSelectorBars();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncSelectorBars();

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (
            e.PropertyName
            is nameof(AppearanceSubpageViewModel.Theme)
                or nameof(AppearanceSubpageViewModel.Density)
        )
        {
            SyncSelectorBars();
        }
    }

    /// <summary>
    /// Push the VM's current Theme + Density into the matching SelectorBar
    /// items without re-firing the SelectionChanged handlers (they'd ping-pong
    /// with the VM setters).
    /// </summary>
    private void SyncSelectorBars()
    {
        if (_viewModel is null)
        {
            return;
        }

        _suppressSelectionEvents = true;
        try
        {
            ThemeBar.SelectedItem = _viewModel.Theme switch
            {
                AppTheme.Light => ThemeLight,
                AppTheme.Dark => ThemeDark,
                _ => ThemeSystem,
            };
            DensityBar.SelectedItem =
                _viewModel.Density == Density.Compact ? DensityCompact : DensityComfortable;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private void OnThemeSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args
    )
    {
        if (_suppressSelectionEvents || _viewModel is null)
            return;
        if (sender.SelectedItem?.Tag is not string tag)
            return;
        _viewModel.Theme = tag switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };
    }

    private void OnDensitySelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args
    )
    {
        if (_suppressSelectionEvents || _viewModel is null)
            return;
        if (sender.SelectedItem?.Tag is not string tag)
            return;
        _viewModel.Density = tag == "Compact" ? Density.Compact : Density.Comfortable;
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (sender is Button btn && btn.Tag is AccentChoice accent)
        {
            _viewModel.AccentChoice = accent;
        }
    }
}
