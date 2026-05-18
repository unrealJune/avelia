using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Controls;
using Avelia.Shell.Windows.Dialogs;
using Avelia.Shell.Windows.Helpers;
using Avelia.Shell.Windows.Pages;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows;

/// <summary>
/// Main window. Hosts the Mica chrome (Chunk 0) plus the TabView title bar +
/// NavigationView rail + Frame layout (Chunk 2). Code-behind wiring is kept
/// thin: forward UI events into the view-model, refresh the rail when the
/// repo tree mutates.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ThemeService _themeService;
    private readonly WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper = new();
    private readonly AveliaServices _services;
    private readonly IUiDispatcher _uiDispatcher;

    /// <summary>
    /// Re-entry guard for the rail pane events. <see cref="ApplyRailDisplayMode"/>
    /// sets <c>NavigationView.PaneDisplayMode</c> programmatically; depending on the
    /// host build, that mutation can itself fire <c>PaneOpening</c>/<c>PaneClosing</c>
    /// which would otherwise call back into <c>ToggleRailCommand</c> and undo the
    /// VM change. Setting this flag for the duration of the programmatic mutation
    /// short-circuits the handlers.
    /// </summary>
    private bool _suppressRailEvents;

    public MainViewModel ViewModel { get; }

    public MainWindow(ThemeService themeService, AveliaServices services)
    {
        _themeService = themeService;
        _services = services;
        ViewModel = new MainViewModel(services);

        InitializeComponent();

        _uiDispatcher = new DispatcherQueueUiDispatcher(DispatcherQueue);

        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TrySetSystemBackdrop();

        ApplyTheme(_themeService.EffectiveTheme);
        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.AccentChanged += OnAccentChanged;
        ApplyAccent(_themeService.AccentHex);

        // Keep the rail tree in sync with ViewModel.RepoGroups.
        ViewModel.RepoGroups.CollectionChanged += OnRepoGroupsChanged;

        // Mirror IsRailExpanded → PaneDisplayMode. Doing this in code-behind
        // keeps the VM free of Microsoft.UI.Xaml types (which would prevent
        // it from compiling in the net10.0 test project).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.OpenAddRepoDialogRequested += OnOpenAddRepoDialogRequested;
        ApplyRailDisplayMode();

        RailNav.SelectedItem = HomeItem;
        NavigateToSection(NavRailSection.Home);

        Closed += OnClosed;

        _ = InitializeViewModelSafelyAsync();
    }

    /// <summary>
    /// Run <see cref="MainViewModel.InitializeAsync"/> with a top-level catch so
    /// any exception is logged rather than silently lost as an unobserved task.
    /// Stub services don't throw today; the real backend (Chunk 10) will.
    /// </summary>
    private async Task InitializeViewModelSafelyAsync()
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] ViewModel.InitializeAsync failed: {ex}");
        }
    }

    // -------- Lifecycle --------

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        _themeService.AccentChanged -= OnAccentChanged;
        ViewModel.RepoGroups.CollectionChanged -= OnRepoGroupsChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.OpenAddRepoDialogRequested -= OnOpenAddRepoDialogRequested;
    }

    // -------- Add repository dialog --------

    /// <summary>
    /// Build and show the AddRepositoryDialog. The dialog owns its VM
    /// lifetime; we just subscribe to <c>RepositoryAdded</c> so the rail tree
    /// picks up the new entry. Fire-and-forget is safe here — exceptions are
    /// logged and the dialog handles its own error UI.
    /// </summary>
    private async void OnOpenAddRepoDialogRequested(object? sender, EventArgs e)
    {
        try
        {
            var dialog = new AddRepositoryDialog(_services, this);
            dialog.RepositoryAdded += OnRepositoryAddedFromDialog;
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                dialog.RepositoryAdded -= OnRepositoryAddedFromDialog;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] AddRepositoryDialog failed: {ex}");
        }
    }

    private void OnRepositoryAddedFromDialog(object? sender, Repository repo)
    {
        ViewModel.AppendRepository(repo);
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(MainViewModel.IsRailExpanded))
        {
            ApplyRailDisplayMode();
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            if (ViewModel.ActiveSection == NavRailSection.Home)
            {
                NavigateToActiveWorkspace();
            }
            return;
        }
    }

    /// <summary>
    /// Navigate the content Frame to the <see cref="WorkspacePage"/> bound to
    /// the active tab's workspace, or to the empty-state placeholder if no tab
    /// is open. Called from both <see cref="NavigateToSection"/> and the
    /// ActiveTab property-change handler.
    /// </summary>
    private void NavigateToActiveWorkspace()
    {
        var active = ViewModel.ActiveTab;
        if (active is null)
        {
            ContentFrame.Navigate(
                typeof(PlaceholderPage),
                new PlaceholderPageArgs(
                    "No workspace open",
                    "Open a workspace from the rail to start a session."
                )
            );
            return;
        }

        var args = new WorkspacePageArgs(active.Id, _services, _uiDispatcher);
        ContentFrame.Navigate(typeof(WorkspacePage), args, new DrillInNavigationTransitionInfo());
    }

    private void ApplyRailDisplayMode()
    {
        _suppressRailEvents = true;
        try
        {
            RailNav.PaneDisplayMode = ViewModel.IsRailExpanded
                ? NavigationViewPaneDisplayMode.Left
                : NavigationViewPaneDisplayMode.LeftCompact;
        }
        finally
        {
            _suppressRailEvents = false;
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        ApplyTheme(_themeService.EffectiveTheme);
    }

    private void ApplyTheme(AppTheme effective)
    {
        RootGrid.RequestedTheme = effective switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnAccentChanged(object? sender, string hex) => ApplyAccent(hex);

    /// <summary>
    /// Push the user's chosen accent into every theme dictionary at runtime.
    /// All variants get the same color — the picker is one swatch, not a
    /// per-theme palette. The walk goes through <c>MergedDictionaries</c>
    /// because <c>Tokens.xaml</c> is itself merged-in; a lookup against the
    /// root resources or <c>Application.Current.Resources.ThemeDictionaries</c>
    /// would miss the key. Fresh SolidColorBrush per dictionary so theme-flip
    /// repaints land on the correct instance.
    /// </summary>
    private static void ApplyAccent(string hex)
    {
        if (!HexColor.TryParse(hex, out var color))
        {
            return;
        }
        foreach (var dict in ThemeResources.EnumerateThemeDictionaries())
        {
            if (dict.ContainsKey("AveliaAccentDefaultBrush"))
            {
                dict["AveliaAccentDefaultBrush"] = new SolidColorBrush(color);
            }
        }
    }

    private void TrySetSystemBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            return;
        }
        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    // -------- TabView events --------

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is WorkspaceTabViewModel tab)
        {
            ViewModel.CloseTabCommand.Execute(tab);
        }
    }

    // -------- Title-bar tools --------

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _themeService.Theme = _themeService.Theme switch
        {
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.Dark, // System → Dark on first click
        };
    }

    // -------- NavigationView events --------

    private void OnRailSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            if (item.Tag is WorkspaceItemViewModel ws)
            {
                ViewModel.OpenWorkspaceCommand.Execute(ws.Id);
                return;
            }

            if (
                item.Tag is string sectionTag
                && Enum.TryParse<NavRailSection>(sectionTag, out var section)
            )
            {
                ViewModel.NavigateSectionCommand.Execute(section);
                NavigateToSection(section);
                return;
            }
        }
    }

    /// <summary>
    /// Catches invokes on rail items whose <c>SelectsOnInvoked</c> is false —
    /// today, just the "Add repository" entry. SelectionChanged ignores them
    /// because the selection never moves; routing through ItemInvoked keeps
    /// the rail's existing one-handler-per-section convention intact.
    /// </summary>
    private void OnRailItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (
            args.InvokedItemContainer is NavigationViewItem item
            && item.Tag is string tag
            && tag == "AddRepo"
        )
        {
            ViewModel.OpenAddRepoDialogCommand.Execute(null);
        }
    }

    private void OnRailPaneOpening(NavigationView sender, object args)
    {
        if (_suppressRailEvents)
        {
            return;
        }
        if (!ViewModel.IsRailExpanded)
        {
            ViewModel.ToggleRailCommand.Execute(null);
        }
    }

    private void OnRailPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (_suppressRailEvents)
        {
            return;
        }
        if (ViewModel.IsRailExpanded)
        {
            ViewModel.ToggleRailCommand.Execute(null);
        }
    }

    // -------- Rail repo tree sync --------

    private void OnRepoGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildRepoTreeItems();
    }

    /// <summary>
    /// Rebuild the runtime-inserted repository tree inside the rail. We can't
    /// declaratively bind nested <c>NavigationView.MenuItems</c> via <c>x:Bind</c>
    /// today, so we keep them in sync from code in response to collection events.
    /// </summary>
    private void RebuildRepoTreeItems()
    {
        // Remove items we previously inserted (identified by their Tag type).
        var toRemove = new List<object>();
        foreach (var item in RailNav.MenuItems)
        {
            if (item is NavigationViewItem nav && nav.Tag is RepoGroupViewModel)
            {
                toRemove.Add(nav);
            }
        }
        foreach (var x in toRemove)
        {
            RailNav.MenuItems.Remove(x);
        }

        // Find the "Repositories" header — we insert groups immediately after it.
        var insertIndex = -1;
        for (var i = 0; i < RailNav.MenuItems.Count; i++)
        {
            if (
                RailNav.MenuItems[i] is NavigationViewItemHeader h
                && h.Content is string s
                && s == "Repositories"
            )
            {
                insertIndex = i + 1;
                break;
            }
        }
        if (insertIndex < 0)
        {
            return;
        }

        foreach (var group in ViewModel.RepoGroups)
        {
            RailNav.MenuItems.Insert(insertIndex++, BuildRepoNavItem(group));
        }
    }

    /// <summary>
    /// Builds a NavigationViewItem for a repository: muted secondary-text
    /// name on the left, workspace count on the right, expanded by default
    /// when <see cref="RepoGroupViewModel.IsExpanded"/> is <c>true</c>.
    /// Children render via <see cref="WorkspaceTreeItem"/>.
    /// </summary>
    private NavigationViewItem BuildRepoNavItem(RepoGroupViewModel group)
    {
        var content = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        );
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Foreground brushes are routed through Styles holding ThemeResource
        // references so the labels re-resolve on theme switch. Setting
        // Foreground directly here would freeze the brush at first paint.
        var nameText = new TextBlock
        {
            Text = group.Name,
            Style = (Style)Application.Current.Resources["AveliaRepoGroupNameStyle"],
        };
        Grid.SetColumn(nameText, 0);
        content.Children.Add(nameText);

        if (group.Workspaces.Count > 0)
        {
            var countText = new TextBlock
            {
                Text = group.Workspaces.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                Style = (Style)Application.Current.Resources["AveliaRepoGroupCountStyle"],
            };
            Grid.SetColumn(countText, 1);
            content.Children.Add(countText);
        }

        var repoItem = new NavigationViewItem
        {
            Content = content,
            Tag = group,
            IsExpanded = group.IsExpanded,
            SelectsOnInvoked = false,
        };
        AutomationProperties.SetName(repoItem, group.Name);

        foreach (var ws in group.Workspaces)
        {
            var wsItem = new NavigationViewItem
            {
                Content = new WorkspaceTreeItem { Item = ws },
                Tag = ws,
            };
            AutomationProperties.SetName(wsItem, ws.Branch);
            repoItem.MenuItems.Add(wsItem);
        }
        return repoItem;
    }

    // -------- Frame navigation --------

    private void NavigateToSection(NavRailSection section)
    {
        switch (section)
        {
            case NavRailSection.Home:
                NavigateToActiveWorkspace();
                break;
            case NavRailSection.Inbox:
                NavigateToInbox();
                break;
            case NavRailSection.Pinned:
                ContentFrame.Navigate(
                    typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Pinned", "Pinned workspaces ship in a later chunk.")
                );
                break;
            case NavRailSection.History:
                ContentFrame.Navigate(
                    typeof(PlaceholderPage),
                    new PlaceholderPageArgs(
                        "History",
                        "Recently closed workspaces ship in a later chunk."
                    )
                );
                break;
            case NavRailSection.Archive:
                ContentFrame.Navigate(
                    typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Archive", "Archived workspaces ship in a later chunk.")
                );
                break;
            case NavRailSection.Settings:
                NavigateToSettings();
                break;
        }
    }

    private void NavigateToSettings()
    {
        var args = new SettingsPageArgs(
            _services,
            _themeService,
            // Single source of truth: change the rail selection. Its
            // SelectionChanged handler runs NavigateSectionCommand + frame nav,
            // so we don't have to invoke either explicitly here.
            BackAction: () => RailNav.SelectedItem = HomeItem
        );
        ContentFrame.Navigate(typeof(SettingsPage), args, new DrillInNavigationTransitionInfo());
    }

    private void NavigateToInbox()
    {
        // The page publishes WorkspaceOpenRequested when a row with a non-empty
        // linked workspace is clicked. We await OpenWorkspaceCommand so the
        // workspace tab and ActiveTab are settled BEFORE flipping the rail —
        // otherwise (with a real-backend's async I/O) the rail flip would fire
        // NavigateToActiveWorkspace against the previous ActiveTab for a beat.
        var args = new InboxPageArgs(
            _services,
            OnWorkspaceOpenRequested: async id =>
            {
                await ViewModel.OpenWorkspaceCommand.ExecuteAsync(id);
                RailNav.SelectedItem = HomeItem;
            }
        );
        ContentFrame.Navigate(typeof(InboxPage), args, new DrillInNavigationTransitionInfo());
    }
}
