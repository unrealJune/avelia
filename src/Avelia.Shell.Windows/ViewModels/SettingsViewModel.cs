using System;
using System.Collections.ObjectModel;
using Avelia.Core;
using Avelia.Shell.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// View-model for <c>SettingsPage</c>. Holds the side-nav list and the
/// currently-active subpage section. Subpage view-models are constructed
/// eagerly so they're ready to bind before the user clicks.
///
/// Owns the three real subpage VMs; <see cref="Dispose"/> cascades into them
/// so any service-event subscriptions (e.g. <c>ThemeService.ThemeChanged</c>)
/// get unhooked when the page navigates away.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AveliaServices _services;
    private readonly ThemeService _themeService;
    private bool _disposed;

    /// <summary>
    /// Definitions for every side-nav row. Static so the list is allocated
    /// once per process, not once per <c>SettingsViewModel</c> construction.
    /// </summary>
    private static readonly (
        SettingsSection Section,
        string Label,
        string Glyph
    )[] SectionDefinitions = new[]
    {
        (SettingsSection.Appearance, "Appearance", ""),
        (SettingsSection.Agents, "Agents & Models", ""),
        (SettingsSection.Repositories, "Repositories", ""),
        (SettingsSection.Profile, "Profile", ""),
        (SettingsSection.Keyboard, "Keyboard", ""),
        (SettingsSection.Notifications, "Notifications", ""),
        (SettingsSection.Privacy, "Privacy", ""),
        (SettingsSection.Updates, "Updates", ""),
        (SettingsSection.About, "About", ""),
    };

    public SettingsViewModel(AveliaServices services, ThemeService themeService)
    {
        _services = services;
        _themeService = themeService;

        Appearance = new AppearanceSubpageViewModel(services, themeService);
        Agents = new AgentsSubpageViewModel(services);
        Profile = new ProfileSubpageViewModel();

        foreach (var def in SectionDefinitions)
        {
            Sections.Add(new SettingsSectionItem(def.Section, def.Label, def.Glyph));
        }

        ActiveSection = SettingsSection.Appearance;
        UpdateActiveFlags();
    }

    /// <summary>The nine items rendered in the side-nav rail.</summary>
    public ObservableCollection<SettingsSectionItem> Sections { get; } = new();

    [ObservableProperty]
    private SettingsSection _activeSection;

    public AppearanceSubpageViewModel Appearance { get; }
    public AgentsSubpageViewModel Agents { get; }
    public ProfileSubpageViewModel Profile { get; }

    /// <summary>
    /// Load the persisted settings into the subpage VMs. Run once when the
    /// page is shown; safe to re-run (the VMs idempotently re-bind to the
    /// current snapshot).
    /// </summary>
    public async System.Threading.Tasks.Task LoadAsync(
        System.Threading.CancellationToken ct = default
    )
    {
        await Appearance.LoadAsync(ct).ConfigureAwait(true);
        await Agents.LoadAsync(ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectSection(SettingsSection section) => ActiveSection = section;

    partial void OnActiveSectionChanged(SettingsSection value) => UpdateActiveFlags();

    private void UpdateActiveFlags()
    {
        foreach (var section in Sections)
        {
            section.IsActive = section.Section == ActiveSection;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Appearance.Dispose();
    }
}

/// <summary>
/// Side-nav sections rendered on Settings. The first four have real content
/// (Chunk 5); the rest route to <c>PlaceholderSubpage</c>.
/// </summary>
public enum SettingsSection
{
    Appearance,
    Agents,
    Repositories,
    Profile,
    Keyboard,
    Notifications,
    Privacy,
    Updates,
    About,
}

/// <summary>
/// One row in the Settings side-nav. <see cref="IsActive"/> is mutated by
/// <see cref="SettingsViewModel"/> when the user changes section; the XAML
/// template binds its accent-bar visibility to it via <c>x:Bind</c>, so no
/// visual-tree walks are needed.
/// </summary>
public partial class SettingsSectionItem : ObservableObject
{
    public SettingsSectionItem(SettingsSection section, string label, string glyph)
    {
        Section = section;
        Label = label;
        Glyph = glyph;
    }

    public SettingsSection Section { get; }
    public string Label { get; }
    public string Glyph { get; }

    [ObservableProperty]
    private bool _isActive;
}
