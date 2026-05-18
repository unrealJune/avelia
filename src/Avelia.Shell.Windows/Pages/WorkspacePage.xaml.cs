using System;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Avelia.Shell.Windows.Pages;

/// <summary>
/// Three-pane workspace view. Chunk 3 ships the center pane (pivot + chat
/// transcript + composer); the right pane is a placeholder for Chunk 4. The
/// page holds a single <see cref="WorkspaceViewModel"/> for its lifetime;
/// <see cref="OnNavigatedTo"/> calls <c>LoadAsync</c> against the parameter
/// workspace (rebuilding the transcript and starting a fresh subscription),
/// <see cref="OnNavigatedFrom"/> cancels the subscription synchronously.
/// </summary>
public sealed partial class WorkspacePage : Page
{
    private WorkspacePageArgs? _pendingArgs;

    public WorkspacePage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The view-model is created on the first navigation (we need the
    /// service graph + dispatcher carried in <c>WorkspacePageArgs</c>).
    /// Subsequent navigations reuse the same instance.
    /// </summary>
    public WorkspaceViewModel? ViewModel { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not WorkspacePageArgs args)
        {
            return;
        }
        _pendingArgs = args;

        if (ViewModel is null)
        {
            ViewModel = new WorkspaceViewModel(args.Services, args.Dispatcher);
            ViewModel.PrPane.FileOpened += OnPrPaneFileOpened;
            Bindings.Update();
        }

        try
        {
            await ViewModel.LoadAsync(args.WorkspaceId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WorkspacePage] LoadAsync failed: {ex}");
        }
    }

    /// <summary>
    /// Bubble a file-row click in the right pane up to the Frame as a navigation
    /// to <see cref="PrReviewPage"/> with the file pre-selected. Uses the page's
    /// <c>Frame</c> directly (set by WinUI when the page is hosted) rather than
    /// reaching into <c>MainWindow</c>; keeps navigation a local concern.
    /// </summary>
    private void OnPrPaneFileOpened(object? sender, RelativePath path)
    {
        if (_pendingArgs is null || Frame is null)
        {
            return;
        }
        var args = new PrReviewPageArgs(_pendingArgs.WorkspaceId, _pendingArgs.Services, path);
        Frame.Navigate(typeof(PrReviewPage), args, new DrillInNavigationTransitionInfo());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Cooperative stop — synchronous, no async/await. The VM's observe
        // task self-completes via the channel's cancellation registration.
        // Any straggling completion is awaited by the next LoadAsync call.
        ViewModel?.StopObserving();
    }

    private void OnPrPaneTabSelectionChanged(
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
        ViewModel.PrPane.ActiveTab = item.Text;
    }
}

/// <summary>Frame-navigation parameter for <see cref="WorkspacePage"/>.</summary>
public sealed record WorkspacePageArgs(
    WorkspaceId WorkspaceId,
    AveliaServices Services,
    IUiDispatcher Dispatcher
);
