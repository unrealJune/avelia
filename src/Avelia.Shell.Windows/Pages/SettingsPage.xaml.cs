using System;
using Avelia.Core;
using Avelia.Shell.Windows.Pages.SettingsSubpages;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Avelia.Shell.Windows.Pages;

/// <summary>
/// Settings shell — side-nav rail on the left, subpage content on the right.
/// Constructed once per page-navigation; receives services and the back-action
/// via <see cref="SettingsPageArgs"/>. The active subpage swaps in
/// <see cref="ContentHost"/> on every <see cref="SettingsViewModel.ActiveSection"/>
/// change — no caching, since the subpage UserControls are cheap to construct
/// and caching would hold stale references to a previous-VM's bindings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private Action? _backAction;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel? ViewModel { get; private set; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not SettingsPageArgs args)
        {
            return;
        }

        // Re-navigation: tear down the previous VM so its ThemeService
        // subscription unhooks before we wire up a fresh one.
        DisposeViewModel();

        _backAction = args.BackAction;
        ViewModel = new SettingsViewModel(args.Services, args.ThemeService);
        Bindings.Update();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateContentForSection(ViewModel.ActiveSection);

        _ = LoadSafelyAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DisposeViewModel();
    }

    private void DisposeViewModel()
    {
        if (ViewModel is null)
            return;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
        ViewModel = null;
    }

    /// <summary>
    /// Wraps <c>LoadAsync</c> in try/catch + log so a settings-service failure
    /// doesn't fire as an unobserved task exception. Stubs don't throw today;
    /// real persistence (Chunk 10) will.
    /// </summary>
    private async System.Threading.Tasks.Task LoadSafelyAsync()
    {
        if (ViewModel is null)
            return;
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] LoadAsync failed: {ex}");
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(SettingsViewModel.ActiveSection) && ViewModel is not null)
        {
            UpdateContentForSection(ViewModel.ActiveSection);
        }
    }

    private void UpdateContentForSection(SettingsSection section)
    {
        if (ViewModel is null)
            return;
        ContentHost.Child = section switch
        {
            SettingsSection.Appearance => new AppearanceSubpage
            {
                ViewModel = ViewModel.Appearance,
            },
            SettingsSection.Agents => new AgentsSubpage { ViewModel = ViewModel.Agents },
            SettingsSection.Profile => new ProfileSubpage { ViewModel = ViewModel.Profile },
            _ => new PlaceholderSubpage(LabelFor(section)),
        };
    }

    private static string LabelFor(SettingsSection s) =>
        s switch
        {
            SettingsSection.Repositories => "Repositories",
            SettingsSection.Keyboard => "Keyboard",
            SettingsSection.Notifications => "Notifications",
            SettingsSection.Privacy => "Privacy",
            SettingsSection.Updates => "Updates",
            SettingsSection.About => "About",
            _ => s.ToString(),
        };

    private void OnSectionItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SettingsSection s)
        {
            ViewModel?.SelectSectionCommand.Execute(s);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _backAction?.Invoke();
    }
}

/// <summary>
/// Navigation parameter for <see cref="SettingsPage"/>. Holds the service
/// graph + theme service and the back-action the page calls when the user
/// hits the back chevron.
/// </summary>
public sealed record SettingsPageArgs(
    AveliaServices Services,
    ThemeService ThemeService,
    Action BackAction
);
