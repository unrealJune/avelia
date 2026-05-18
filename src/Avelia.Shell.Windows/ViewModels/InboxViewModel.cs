using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// View-model for <c>InboxPage</c>. Loads inbox items from
/// <see cref="IInboxService.ListAsync"/>, projects them into row VMs, and exposes
/// an open command that publishes <see cref="WorkspaceOpenRequested"/> for the
/// shell to translate into <c>MainViewModel.OpenWorkspace</c>.
///
/// The seeded data has no "read" state yet; until persistence lands every item
/// is treated as unread so the header reads "N unread · N total".
/// </summary>
public partial class InboxViewModel : ObservableObject
{
    private readonly AveliaServices _services;

    /// <summary>
    /// Lifecycle token for <see cref="LoadAsync"/>. Mirrors the in-flight
    /// guard pattern in <c>PrReviewViewModel.LoadAsync</c>: a second call
    /// cancels the first so two near-simultaneous loads can't tear the
    /// observable state.
    /// </summary>
    private CancellationTokenSource? _loadCts;

    public InboxViewModel(AveliaServices services)
    {
        _services = services;
        OpenCommand = new RelayCommand<InboxItemViewModel?>(OnOpen);
    }

    // -------- Observable state --------

    public ObservableCollection<InboxItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private int _unreadCount;

    /// <summary>"N unread · N total" — empty when no items loaded yet.</summary>
    public string HeaderSubtitle =>
        TotalCount == 0 ? string.Empty : $"{UnreadCount} unread · {TotalCount} total";

    // -------- Events --------

    /// <summary>
    /// Raised when a row's open affordance fires and the row has a non-empty
    /// linked workspace. The shell subscribes and routes the id into
    /// <see cref="MainViewModel.OpenWorkspaceCommand"/>. Args carry the
    /// workspace id only — the inbox row is otherwise opaque to the shell.
    /// </summary>
    public event EventHandler<WorkspaceId>? WorkspaceOpenRequested;

    // -------- Commands --------

    /// <summary>
    /// Invoked from the row click in the page. The parent owns the command
    /// (not the row VM) so there's exactly one publisher for
    /// <see cref="WorkspaceOpenRequested"/>.
    /// </summary>
    public IRelayCommand<InboxItemViewModel?> OpenCommand { get; }

    private void OnOpen(InboxItemViewModel? item)
    {
        if (item is null || !item.HasLinkedWorkspace)
        {
            return;
        }
        WorkspaceOpenRequested?.Invoke(this, item.LinkedWorkspaceId);
    }

    // -------- Load --------

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;

        Items.Clear();
        TotalCount = 0;
        UnreadCount = 0;

        try
        {
            var items = await _services.Inbox.ListAsync(token).ConfigureAwait(true);
            foreach (var item in items)
            {
                Items.Add(new InboxItemViewModel(item));
            }
            TotalCount = items.Count;
            // No IsRead field on the F# record yet; treat every item as unread
            // until persistence (Chunk 10) tracks read state.
            UnreadCount = items.Count;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later LoadAsync — the new call already reset
            // the observable state.
        }
    }
}
