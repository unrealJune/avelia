using System;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Controls;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Avelia.Shell.Windows.Pages;

/// <summary>
/// PR Review page — file tree on the left, unified diff viewer on the right,
/// title bar with PR# + status pill + merge action up top, review actions at
/// the bottom. Reached from <c>WorkspacePage</c>'s file-row click; back nav
/// uses the Frame's BackStack which returns to the originating workspace.
///
/// The page constructs its <see cref="ViewModel"/> on first navigation; later
/// navigations with a different workspace re-run <see cref="LoadAsync"/>
/// against the same VM, mirroring <c>WorkspacePage</c>'s page-lifecycle
/// pattern.
/// </summary>
public sealed partial class PrReviewPage : Page
{
    public PrReviewPage()
    {
        InitializeComponent();
    }

    public PrReviewViewModel? ViewModel { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PrReviewPageArgs args)
        {
            return;
        }

        if (ViewModel is null)
        {
            ViewModel = new PrReviewViewModel(args.Services);
            Bindings.Update();
        }

        try
        {
            await ViewModel.LoadAsync(args.WorkspaceId, args.PreselectFile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PrReviewPage] LoadAsync failed: {ex}");
        }
    }

    private async void OnFileSelected(object sender, DiffFileViewModel file)
    {
        if (ViewModel is null)
        {
            return;
        }
        try
        {
            await ViewModel.SelectFileAsync(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PrReviewPage] SelectFileAsync failed: {ex}");
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame is not null && Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}

/// <summary>Frame-navigation parameter for <see cref="PrReviewPage"/>.</summary>
public sealed record PrReviewPageArgs(
    WorkspaceId WorkspaceId,
    AveliaServices Services,
    RelativePath? PreselectFile = null
);
