using System.Linq;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class SettingsViewModelTests
{
    private static (AveliaServices services, ThemeService theme) MakeContext()
    {
        var services = Composition.buildStubServices();
        var theme = new ThemeService(systemThemeProvider: () => AppTheme.Dark);
        return (services, theme);
    }

    [Fact]
    public void Sections_ContainsAllNineDesignSections()
    {
        var (services, theme) = MakeContext();
        var vm = new SettingsViewModel(services, theme);

        Assert.Equal(9, vm.Sections.Count);
        Assert.Contains(vm.Sections, s => s.Section == SettingsSection.Appearance);
        Assert.Contains(vm.Sections, s => s.Section == SettingsSection.Agents);
        Assert.Contains(vm.Sections, s => s.Section == SettingsSection.Profile);
        Assert.Contains(vm.Sections, s => s.Section == SettingsSection.About);
    }

    [Fact]
    public void Default_ActiveSection_IsAppearance()
    {
        var (services, theme) = MakeContext();
        var vm = new SettingsViewModel(services, theme);

        Assert.Equal(SettingsSection.Appearance, vm.ActiveSection);
    }

    [Fact]
    public void SelectSection_UpdatesActiveSection()
    {
        var (services, theme) = MakeContext();
        var vm = new SettingsViewModel(services, theme);

        vm.SelectSectionCommand.Execute(SettingsSection.Profile);

        Assert.Equal(SettingsSection.Profile, vm.ActiveSection);
    }

    [Fact]
    public void SelectSection_FlipsMatchingSectionItemIsActive()
    {
        var (services, theme) = MakeContext();
        var vm = new SettingsViewModel(services, theme);

        vm.SelectSectionCommand.Execute(SettingsSection.Agents);

        var agents = vm.Sections.First(s => s.Section == SettingsSection.Agents);
        var appearance = vm.Sections.First(s => s.Section == SettingsSection.Appearance);
        Assert.True(agents.IsActive);
        Assert.False(appearance.IsActive);
        // Exactly one item should be active at any time.
        Assert.Equal(1, vm.Sections.Count(s => s.IsActive));
    }

    [Fact]
    public void Dispose_CascadesToAppearanceSubpageVm()
    {
        var (services, theme) = MakeContext();
        var vm = new SettingsViewModel(services, theme);
        var observedThemeNotifications = 0;
        vm.Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSubpageViewModel.Theme))
            {
                observedThemeNotifications++;
            }
        };

        vm.Dispose();
        theme.Theme = AppTheme.Light;

        // After Dispose the cascade unhooks the bridge between ThemeService and
        // AppearanceSubpageViewModel, so the Theme PropertyChanged fan-out stops.
        Assert.Equal(0, observedThemeNotifications);
    }

    [Fact]
    public async Task LoadAsync_HydratesAppearanceFromSettingsService()
    {
        var (services, theme) = MakeContext();
        // Pre-seed the settings service so loading observes the change.
        await services.Settings.SetAccentAsync(AccentChoice.Violet, CancellationToken.None);
        await services.Settings.SetDensityAsync(Density.Compact, CancellationToken.None);

        var vm = new SettingsViewModel(services, theme);
        await vm.LoadAsync();

        Assert.Equal(AccentChoice.Violet, vm.Appearance.AccentChoice);
        Assert.Equal(Density.Compact, vm.Appearance.Density);
    }
}

public class AppearanceSubpageViewModelTests
{
    private static (AveliaServices services, ThemeService theme) MakeContext()
    {
        var services = Composition.buildStubServices();
        var theme = new ThemeService(systemThemeProvider: () => AppTheme.Dark);
        return (services, theme);
    }

    [Fact]
    public void AccentSwatches_HasAllSixDesignAccents()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);

        Assert.Equal(6, vm.AccentSwatches.Count);
        Assert.Contains(vm.AccentSwatches, s => Equals(s.Choice, AccentChoice.SkyBlue));
        Assert.Contains(vm.AccentSwatches, s => Equals(s.Choice, AccentChoice.Sage));
    }

    [Fact]
    public async Task SettingAccentChoice_FlipsMatchingSwatchIsSelected()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        await vm.LoadAsync();

        vm.AccentChoice = AccentChoice.Magenta;

        Assert.True(
            vm.AccentSwatches.First(s => Equals(s.Choice, AccentChoice.Magenta)).IsSelected
        );
        Assert.All(
            vm.AccentSwatches.Where(s => !Equals(s.Choice, AccentChoice.Magenta)),
            s => Assert.False(s.IsSelected)
        );
    }

    [Fact]
    public void Dispose_UnhooksThemeChanged()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        var raisedAfterDispose = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSubpageViewModel.Theme))
            {
                raisedAfterDispose++;
            }
        };

        vm.Dispose();
        theme.Theme = AppTheme.Light;

        // The VM still has a direct PropertyChanged subscriber (the test), but
        // since the ThemeService→VM bridge is gone, the bridge no longer raises.
        Assert.Equal(0, raisedAfterDispose);
    }

    [Fact]
    public void SettingTheme_UpdatesThemeService()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);

        vm.Theme = AppTheme.Light;

        Assert.Equal(AppTheme.Light, theme.Theme);
    }

    [Fact]
    public void ThemeService_ThemeChanged_PropagatesToViewModelTheme()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        var raisedFor = (string?)null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSubpageViewModel.Theme))
            {
                raisedFor = e.PropertyName;
            }
        };

        theme.Theme = AppTheme.Light;

        Assert.Equal(nameof(AppearanceSubpageViewModel.Theme), raisedFor);
        Assert.Equal(AppTheme.Light, vm.Theme);
    }

    [Fact]
    public async Task SettingAccentChoice_PushesHexToThemeService()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        await vm.LoadAsync();
        string? observedHex = null;
        theme.AccentChanged += (_, hex) => observedHex = hex;

        vm.AccentChoice = AccentChoice.Sage;

        Assert.Equal(AccentChoice.Sage.Hex, theme.AccentHex);
        Assert.Equal(AccentChoice.Sage.Hex, observedHex);
    }

    [Fact]
    public async Task SettingDensity_PersistsToSettingsService()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        await vm.LoadAsync();

        vm.Density = Density.Compact;

        // Allow the fire-and-forget Task to flush.
        await System.Threading.Tasks.Task.Yield();
        var snapshot = await services.Settings.GetAsync(CancellationToken.None);
        Assert.Equal(Density.Compact, snapshot.Density);
    }

    [Fact]
    public async Task LoadAsync_DoesNotRetriggerPersistenceForCurrentValues()
    {
        var (services, theme) = MakeContext();
        // Snapshot the initial defaults so we can verify they're unchanged.
        var before = await services.Settings.GetAsync(CancellationToken.None);
        var vm = new AppearanceSubpageViewModel(services, theme);

        await vm.LoadAsync();

        // The load shouldn't have re-issued any setters; the snapshot must
        // round-trip exactly.
        var after = await services.Settings.GetAsync(CancellationToken.None);
        Assert.Equal(before.Accent, after.Accent);
        Assert.Equal(before.Density, after.Density);
        Assert.Equal(before.Transparency, after.Transparency);
        Assert.Equal(before.OpenWithRightPanel, after.OpenWithRightPanel);
        Assert.Equal(before.DefaultModel, after.DefaultModel);
    }

    [Fact]
    public async Task SelectAccentCommand_UpdatesAccentChoice()
    {
        var (services, theme) = MakeContext();
        var vm = new AppearanceSubpageViewModel(services, theme);
        await vm.LoadAsync();

        vm.SelectAccentCommand.Execute(AccentChoice.Magenta);

        Assert.Equal(AccentChoice.Magenta, vm.AccentChoice);
    }
}

public class AgentsSubpageViewModelTests
{
    [Fact]
    public void Models_LoadsThreeDesignModels()
    {
        var services = Composition.buildStubServices();
        var vm = new AgentsSubpageViewModel(services);

        Assert.Equal(3, vm.Models.Count);
        Assert.Contains(vm.Models, m => Equals(m.Choice, ModelChoice.Sonnet45));
        Assert.Contains(vm.Models, m => Equals(m.Choice, ModelChoice.Opus41));
        Assert.Contains(vm.Models, m => Equals(m.Choice, ModelChoice.Haiku45));
    }

    [Fact]
    public async Task LoadAsync_SetsSelectedModelToServiceDefault()
    {
        var services = Composition.buildStubServices();
        var vm = new AgentsSubpageViewModel(services);

        await vm.LoadAsync();

        Assert.NotNull(vm.SelectedModel);
        // Default per DesignData.defaultAppearance is Sonnet45.
        Assert.Equal(ModelChoice.Sonnet45, vm.SelectedModel!.Choice);
    }

    [Fact]
    public async Task SettingSelectedModel_PersistsToSettingsService()
    {
        var services = Composition.buildStubServices();
        var vm = new AgentsSubpageViewModel(services);
        await vm.LoadAsync();

        var opus = vm.Models.First(m => Equals(m.Choice, ModelChoice.Opus41));
        vm.SelectedModel = opus;

        await System.Threading.Tasks.Task.Yield();
        var snapshot = await services.Settings.GetAsync(CancellationToken.None);
        Assert.Equal(ModelChoice.Opus41, snapshot.DefaultModel);
    }

    [Fact]
    public async Task SettingExtendedThinking_PersistsToSettingsService()
    {
        var services = Composition.buildStubServices();
        var vm = new AgentsSubpageViewModel(services);
        await vm.LoadAsync();

        vm.ExtendedThinking = true;

        await System.Threading.Tasks.Task.Yield();
        var snapshot = await services.Settings.GetAsync(CancellationToken.None);
        Assert.True(snapshot.ExtendedThinking);
    }
}

public class ProfileSubpageViewModelTests
{
    [Fact]
    public void Initials_DerivedFromTwoWordName()
    {
        var vm = new ProfileSubpageViewModel { DisplayName = "June Philip" };
        Assert.Equal("JP", vm.Initials);
    }

    [Fact]
    public void Initials_FallsBackToFirstTwoChars_ForSingleWordName()
    {
        var vm = new ProfileSubpageViewModel { DisplayName = "Avelia" };
        Assert.Equal("AV", vm.Initials);
    }

    [Fact]
    public void Initials_RecomputedWhenNameChanges()
    {
        var vm = new ProfileSubpageViewModel();
        var raisedForInitials = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileSubpageViewModel.Initials))
            {
                raisedForInitials = true;
            }
        };

        vm.DisplayName = "Ada Lovelace";

        Assert.True(raisedForInitials);
        Assert.Equal("AL", vm.Initials);
    }
}
