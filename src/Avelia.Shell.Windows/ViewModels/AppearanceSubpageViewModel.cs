using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// VM backing the Appearance subpage. Theme is owned by <see cref="ThemeService"/>
/// (platform state — flips ElementTheme live), every other preference is
/// persisted via <see cref="ISettingsService"/>. Setters write through
/// immediately; the UI binds two-way so user gestures land in the service.
///
/// Disposable so the subscription to <c>ThemeService.ThemeChanged</c> unhooks
/// when the page navigates away — otherwise reopening Settings would leak a
/// VM per visit.
/// </summary>
public partial class AppearanceSubpageViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private bool _isLoading;
    private bool _disposed;

    public AppearanceSubpageViewModel(AveliaServices services, ThemeService themeService)
    {
        _settings = services.Settings;
        _theme = themeService;
        _theme.ThemeChanged += OnThemeServiceChanged;

        foreach (var accent in AccentChoice.All)
        {
            AccentSwatches.Add(new AccentSwatchItem(accent));
        }
    }

    // -------- Theme (lives in ThemeService) --------

    public AppTheme Theme
    {
        get => _theme.Theme;
        set
        {
            if (_theme.Theme == value)
                return;
            _theme.Theme = value;
            OnPropertyChanged();
        }
    }

    // -------- Accent (persisted via ISettingsService) --------

    /// <summary>
    /// Six accent swatches in display order. Each item carries its own
    /// <see cref="AccentSwatchItem.IsSelected"/> flag — flipped by this VM when
    /// <see cref="AccentChoice"/> changes, so the swatch DataTemplate can bind
    /// its selection ring via <c>x:Bind</c>.
    /// </summary>
    public ObservableCollection<AccentSwatchItem> AccentSwatches { get; } = new();

    [ObservableProperty]
    private AccentChoice _accentChoice = AccentChoice.SkyBlue;

    [ObservableProperty]
    private Density _density = Density.Comfortable;

    [ObservableProperty]
    private bool _transparency;

    [ObservableProperty]
    private bool _openWithRightPanel;

    // -------- Persistence wiring --------

    /// <summary>
    /// Hydrate state from the settings service. Sets a guard flag so the
    /// generated <c>OnXxxChanged</c> partial-method hooks don't re-write the
    /// same value back through the service while we're loading.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(true);
        _isLoading = true;
        try
        {
            AccentChoice = snapshot.Accent;
            Density = snapshot.Density;
            Transparency = snapshot.Transparency;
            OpenWithRightPanel = snapshot.OpenWithRightPanel;
            _theme.AccentHex = snapshot.Accent.Hex;
        }
        finally
        {
            _isLoading = false;
        }
        UpdateSwatchSelection();
    }

    // The generated [ObservableProperty] machinery calls these on every change.
    // We forward the new value to the service (skipping during LoadAsync) and,
    // for Accent, push the hex into ThemeService so the brush swap fires.
    partial void OnAccentChoiceChanged(AccentChoice value)
    {
        UpdateSwatchSelection();
        if (_isLoading)
            return;
        _theme.AccentHex = value.Hex;
        FireAndForget(
            _settings.SetAccentAsync(value, CancellationToken.None),
            nameof(_settings.SetAccentAsync)
        );
    }

    partial void OnDensityChanged(Density value)
    {
        if (_isLoading)
            return;
        FireAndForget(
            _settings.SetDensityAsync(value, CancellationToken.None),
            nameof(_settings.SetDensityAsync)
        );
    }

    partial void OnTransparencyChanged(bool value)
    {
        if (_isLoading)
            return;
        FireAndForget(
            _settings.SetTransparencyAsync(value, CancellationToken.None),
            nameof(_settings.SetTransparencyAsync)
        );
    }

    partial void OnOpenWithRightPanelChanged(bool value)
    {
        if (_isLoading)
            return;
        FireAndForget(
            _settings.SetOpenWithRightPanelAsync(value, CancellationToken.None),
            nameof(_settings.SetOpenWithRightPanelAsync)
        );
    }

    [RelayCommand]
    private void SelectAccent(AccentChoice accent) => AccentChoice = accent;

    private void OnThemeServiceChanged(object? sender, AppTheme theme) =>
        OnPropertyChanged(nameof(Theme));

    private void UpdateSwatchSelection()
    {
        foreach (var swatch in AccentSwatches)
        {
            swatch.IsSelected = Equals(swatch.Choice, AccentChoice);
        }
    }

    /// <summary>
    /// Awaits a settings write task in the background and logs anything that
    /// throws. Replaces bare <c>_ = SetXxxAsync(...)</c> calls so a service
    /// failure surfaces in the debug log instead of vanishing as an
    /// unobserved task.
    /// </summary>
    private static void FireAndForget(Task task, string op)
    {
        _ = task.ContinueWith(
            t =>
                System.Diagnostics.Debug.WriteLine(
                    $"[AppearanceSubpageViewModel] {op} failed: {t.Exception}"
                ),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _theme.ThemeChanged -= OnThemeServiceChanged;
    }
}

/// <summary>
/// One accent swatch in the Appearance subpage's swatch row. <see cref="Choice"/>
/// is immutable (each swatch represents a fixed accent); <see cref="IsSelected"/>
/// is flipped by <see cref="AppearanceSubpageViewModel"/> when the user picks a
/// different accent, driving the swatch's selection ring via <c>x:Bind</c>.
/// </summary>
public partial class AccentSwatchItem : ObservableObject
{
    public AccentSwatchItem(AccentChoice choice)
    {
        Choice = choice;
    }

    public AccentChoice Choice { get; }

    [ObservableProperty]
    private bool _isSelected;
}
