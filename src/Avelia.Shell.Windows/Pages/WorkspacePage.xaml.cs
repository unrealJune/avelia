using System;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml.Controls;
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Cooperative stop — synchronous, no async/await. The VM's observe
        // task self-completes via the channel's cancellation registration.
        // Any straggling completion is awaited by the next LoadAsync call.
        ViewModel?.StopObserving();
    }
}

/// <summary>Frame-navigation parameter for <see cref="WorkspacePage"/>.</summary>
public sealed record WorkspacePageArgs(
    WorkspaceId WorkspaceId,
    AveliaServices Services,
    IUiDispatcher Dispatcher);
