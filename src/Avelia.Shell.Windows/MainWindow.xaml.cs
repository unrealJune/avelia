using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Controls;
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
        ViewModel = new MainViewModel(services);

        InitializeComponent();

        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TrySetSystemBackdrop();

        ApplyTheme(_themeService.EffectiveTheme);
        _themeService.ThemeChanged += OnThemeChanged;

        // Keep the rail tree in sync with ViewModel.RepoGroups.
        ViewModel.RepoGroups.CollectionChanged += OnRepoGroupsChanged;

        // Mirror IsRailExpanded → PaneDisplayMode. Doing this in code-behind
        // keeps the VM free of Microsoft.UI.Xaml types (which would prevent
        // it from compiling in the net10.0 test project).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        ViewModel.RepoGroups.CollectionChanged -= OnRepoGroupsChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRailExpanded))
        {
            ApplyRailDisplayMode();
        }
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

    private void OnRailSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            if (item.Tag is WorkspaceItemViewModel ws)
            {
                ViewModel.OpenWorkspaceCommand.Execute(ws.Id);
                return;
            }

            if (item.Tag is string sectionTag &&
                Enum.TryParse<NavRailSection>(sectionTag, out var section))
            {
                ViewModel.NavigateSectionCommand.Execute(section);
                NavigateToSection(section);
                return;
            }
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
            if (RailNav.MenuItems[i] is NavigationViewItemHeader h &&
                h.Content is string s && s == "Repositories")
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
        var content = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = group.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AveliaTextSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameText, 0);
        content.Children.Add(nameText);

        if (group.Workspaces.Count > 0)
        {
            var countText = new TextBlock
            {
                Text = group.Workspaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AveliaTextTertiaryBrush"],
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
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs(
                        "Workspace",
                        "The 3-pane workspace view (chat + PR + terminal) ships in Chunks 3 and 4."),
                    new DrillInNavigationTransitionInfo());
                break;
            case NavRailSection.Inbox:
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Inbox", "Inbox notifications ship in Chunk 7."));
                break;
            case NavRailSection.Pinned:
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Pinned", "Pinned workspaces ship in a later chunk."));
                break;
            case NavRailSection.History:
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs("History", "Recently closed workspaces ship in a later chunk."));
                break;
            case NavRailSection.Archive:
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Archive", "Archived workspaces ship in a later chunk."));
                break;
            case NavRailSection.Settings:
                ContentFrame.Navigate(typeof(PlaceholderPage),
                    new PlaceholderPageArgs("Settings", "The Settings page ships in Chunk 5."));
                break;
        }
    }
}
