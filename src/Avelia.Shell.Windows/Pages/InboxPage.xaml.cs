using System;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Pages;

/// <summary>
/// Inbox page — list of notifications (warning / success / info) projected from
/// <see cref="IInboxService.ListAsync"/>. Row click on an item with a linked
/// workspace publishes <see cref="InboxViewModel.WorkspaceOpenRequested"/> which
/// the shell turns into <c>MainViewModel.OpenWorkspace</c>; rows without a
/// linked workspace silently no-op.
///
/// Page constructs its view-model on first navigation; subsequent navigations
/// reuse the same VM and re-run <see cref="InboxViewModel.LoadAsync"/>. The VM
/// holds an in-flight CTS so rapid re-navigation can't tear state.
/// </summary>
public sealed partial class InboxPage : Page
{
    public InboxPage()
    {
        InitializeComponent();
    }

    public InboxViewModel? ViewModel { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not InboxPageArgs args)
        {
            return;
        }

        if (ViewModel is null)
        {
            ViewModel = new InboxViewModel(args.Services);
            ViewModel.WorkspaceOpenRequested += OnWorkspaceOpenRequested;
            Bindings.Update();
        }

        _onWorkspaceOpenRequested = args.OnWorkspaceOpenRequested;

        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InboxPage] LoadAsync failed: {ex}");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach the args-level handler so a future re-navigation can install a
        // fresh one without piling up subscriptions. ViewModel.WorkspaceOpenRequested
        // → OnWorkspaceOpenRequested stays (same VM lifetime as the page).
        _onWorkspaceOpenRequested = null;
    }

    private Func<WorkspaceId, Task>? _onWorkspaceOpenRequested;

    private async void OnWorkspaceOpenRequested(object? sender, WorkspaceId id)
    {
        // Fire-and-forget the shell's open routine: the VM event is an
        // EventHandler so this handler is async void by necessity. Faults bubble
        // up via the catch so a future real-backend exception doesn't crash the
        // process as an unobserved task.
        if (_onWorkspaceOpenRequested is null)
        {
            return;
        }
        try
        {
            await _onWorkspaceOpenRequested(id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InboxPage] OnWorkspaceOpenRequested failed: {ex}"
            );
        }
    }

    private void OnInboxItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel is null || e.ClickedItem is not InboxItemViewModel row)
        {
            return;
        }
        ViewModel.OpenCommand.Execute(row);
    }
}

/// <summary>
/// Frame-navigation parameter for <see cref="InboxPage"/>. The page reaches the
/// shell through <paramref name="OnWorkspaceOpenRequested"/> — a
/// <see cref="Task"/>-returning callback so the shell can await the workspace
/// open before flipping the rail selection. Stub services complete synchronously
/// (no observable timing difference today); real-backend services with async
/// I/O won't briefly flash the previous workspace because the rail flip waits.
/// </summary>
public sealed record InboxPageArgs(
    AveliaServices Services,
    Func<WorkspaceId, Task> OnWorkspaceOpenRequested
);
