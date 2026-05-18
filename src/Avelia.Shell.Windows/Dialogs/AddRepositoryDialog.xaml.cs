using System;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Avelia.Shell.Windows.Dialogs;

/// <summary>
/// "Add repository" modal — three-tab ContentDialog backed by
/// <see cref="AddRepositoryViewModel"/>. The dialog handles WinUI-specific
/// concerns: the SelectorBar↔enum bridge (same constraint as the Appearance
/// subpage's Theme/Density bars), the FolderPicker hand-off for the Browse
/// button, and translating <see cref="AddRepositoryViewModel.RepositoryAdded"/>
/// into <see cref="Hide"/>. The host (MainWindow) subscribes to
/// <see cref="RepositoryAdded"/> to refresh the rail tree.
/// </summary>
public sealed partial class AddRepositoryDialog : ContentDialog
{
    private readonly Window _hostWindow;
    private bool _suppressTabEvents;

    /// <summary>
    /// Raised after the user successfully adds a repo and the dialog is about
    /// to close. The host listens to refresh the rail tree with the new entry.
    /// </summary>
    public event EventHandler<Repository>? RepositoryAdded;

    public AddRepositoryDialog(AveliaServices services, Window hostWindow)
    {
        _hostWindow = hostWindow;
        InitializeComponent();
        // XamlRoot is what carries the host's theme down — ContentDialog
        // inherits ElementTheme from it automatically. Don't snapshot
        // ActualTheme into RequestedTheme: that freezes the dialog's theme
        // at construction (Chunk-5 E-3 bug class) and a runtime theme flip
        // while the dialog is open wouldn't follow.
        XamlRoot = hostWindow.Content.XamlRoot;
        ViewModel = new AddRepositoryViewModel(services);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RepositoryAdded += OnRepositoryAdded;
        Bindings.Update();
        SyncIsAddEnabled();
        SyncTabContent();
        _ = LoadSafelyAsync();
    }

    public AddRepositoryViewModel ViewModel { get; }

    private async System.Threading.Tasks.Task LoadSafelyAsync()
    {
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AddRepositoryDialog] LoadAsync failed: {ex}");
        }
    }

    // -------- Tab routing --------

    private void OnTabSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args
    )
    {
        if (_suppressTabEvents)
        {
            return;
        }
        if (sender.SelectedItem?.Tag is not string tag)
        {
            return;
        }
        ViewModel.ActiveTab = tag switch
        {
            "GitUrl" => AddRepositoryViewModel.Tab.GitUrl,
            "GitHub" => AddRepositoryViewModel.Tab.GitHub,
            _ => AddRepositoryViewModel.Tab.LocalFolder,
        };
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        switch (e.PropertyName)
        {
            case nameof(AddRepositoryViewModel.ActiveTab):
                SyncTabContent();
                SyncIsAddEnabled();
                break;
            case nameof(AddRepositoryViewModel.LocalPath):
            case nameof(AddRepositoryViewModel.GitUrl):
            case nameof(AddRepositoryViewModel.CloneToPath):
            case nameof(AddRepositoryViewModel.SelectedGitHubRepo):
                SyncIsAddEnabled();
                break;
        }
    }

    /// <summary>
    /// Show the right panel + sync the SelectorBar's selected item without
    /// re-firing the SelectionChanged handler (it'd ping-pong with the VM).
    /// </summary>
    private void SyncTabContent()
    {
        LocalFolderPanel.Visibility =
            ViewModel.ActiveTab == AddRepositoryViewModel.Tab.LocalFolder
                ? Visibility.Visible
                : Visibility.Collapsed;
        GitUrlPanel.Visibility =
            ViewModel.ActiveTab == AddRepositoryViewModel.Tab.GitUrl
                ? Visibility.Visible
                : Visibility.Collapsed;
        GitHubPanel.Visibility =
            ViewModel.ActiveTab == AddRepositoryViewModel.Tab.GitHub
                ? Visibility.Visible
                : Visibility.Collapsed;

        _suppressTabEvents = true;
        try
        {
            TabSelector.SelectedItem = ViewModel.ActiveTab switch
            {
                AddRepositoryViewModel.Tab.GitUrl => GitUrlTab,
                AddRepositoryViewModel.Tab.GitHub => GitHubTab,
                _ => LocalFolderTab,
            };
        }
        finally
        {
            _suppressTabEvents = false;
        }
    }

    private void SyncIsAddEnabled()
    {
        IsPrimaryButtonEnabled = ViewModel.AddCommand.CanExecute(null);
    }

    // -------- Browse (FolderPicker) --------

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            // FolderPicker in WinUI 3 needs the parent window's HWND to render.
            var hwnd = WindowNative.GetWindowHandle(_hostWindow);
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.LocalPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            // Folder picker can throw in restricted sandboxes (e.g. test runs).
            // Log + swallow — the user can still type a path manually.
            System.Diagnostics.Debug.WriteLine($"[AddRepositoryDialog] Browse failed: {ex}");
        }
    }

    // -------- Recent repo click --------

    private void OnRecentRepoClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentRepoItem item)
        {
            ViewModel.PickRecent(item);
        }
    }

    // -------- Primary button --------

    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args
    )
    {
        // Don't auto-close — AddAsync may surface a validation error that the
        // user needs to read. Deferral keeps the dialog open until the command
        // either succeeds (which fires RepositoryAdded → Hide) or fails.
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = true; // prevent default close — we'll Hide on success
            await ViewModel.AddCommand.ExecuteAsync(null);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnRepositoryAdded(object? sender, Repository repo)
    {
        RepositoryAdded?.Invoke(this, repo);
        Hide();
    }
}
