using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;
using ValueTask = System.Threading.Tasks.ValueTask;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// View-model for <c>WorkspacePage</c>'s center pane. Loads the workspace's
/// conversation snapshot, subscribes to live <c>MessageEvent</c>s, exposes the
/// projected transcript + composer state + send command.
///
/// Lives only as long as the page; <see cref="DisposeAsync"/> cancels the
/// observe stream and any in-flight send. Tests construct it with an
/// <see cref="ImmediateUiDispatcher"/> so callbacks run synchronously.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AveliaServices _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private CancellationTokenSource? _observeCts;
    private Task? _observeTask;
    private ConversationId? _conversationId;
    private bool _diffRefreshPending;

    /// <summary>
    /// User messages shown optimistically (see <see cref="SendMessage"/>) that
    /// are still awaiting their live <c>ObserveMessages</c> echo. FIFO: the head
    /// is the oldest un-echoed send. Touched only on the UI thread (send runs
    /// there; echoes are marshalled via <see cref="_dispatcher"/>), so it needs
    /// no lock.
    /// </summary>
    private readonly List<UserMessageViewModel> _pendingUserEchoes = new();

    // -------- Turn-grouping projection state (UI thread only) --------
    // A "turn" runs from one user message to the next. Within a turn, tool
    // batches / change notes / superseded agent messages collapse into a single
    // grey AgentActivityGroupViewModel; only the latest result candidate stays
    // surfaced. The final result falls out naturally — nothing supersedes it —
    // so no end-of-turn signal from the backend is required, and the same logic
    // replays cleanly on reload.
    private AgentActivityGroupViewModel? _currentGroup;
    private MessageViewModel? _currentResult;

    public WorkspaceViewModel(
        AveliaServices services,
        IUiDispatcher dispatcher,
        INotificationService? notifications = null
    )
    {
        _services = services;
        _dispatcher = dispatcher;
        _notifications = notifications ?? new NullNotificationService();
        PrPane = new PrPaneViewModel(services);
        Terminal = new TerminalPanelViewModel();
        ModelBar = new ModelBarViewModel();
    }

    /// <summary>Right-pane PR header + workspace file list. Always present; <see cref="PrPaneViewModel.HasPullRequest"/> reflects whether a PR exists.</summary>
    public PrPaneViewModel PrPane { get; }

    /// <summary>Sticky bottom terminal panel — prompt line + tab strip.</summary>
    public TerminalPanelViewModel Terminal { get; }

    /// <summary>
    /// Unified composer model bar — model · reasoning effort · context tier.
    /// The model is seeded from the workspace's <c>Agent</c>; reasoning and
    /// context default to the user's Settings → Agents picks (per-conversation
    /// overrides aren't persisted yet).
    /// </summary>
    public ModelBarViewModel ModelBar { get; }

    // -------- Observable state --------

    /// <summary>Workspace currently being viewed. Set by <see cref="LoadAsync"/>.</summary>
    [ObservableProperty]
    private WorkspaceId? _workspaceId;

    /// <summary>Conversation title (e.g. "Debugging ReferenceError"). Empty until loaded.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Composer text. Bound two-way to the multi-line TextBox.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composerText = string.Empty;

    /// <summary>
    /// Live working-tree status summary for the workspace's worktree
    /// (e.g. "Clean", "3 changed · ↑2 ↓1"). Populated from a real
    /// <c>git status</c> read on load; empty until resolved.
    /// </summary>
    [ObservableProperty]
    private string _gitStatusSummary = string.Empty;

    /// <summary>Indicates a load is in progress; bound to a progress ring.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// True from the moment the user sends a message until the agent produces
    /// its first response event for that turn. Bound to a "Working…" indicator
    /// above the composer so the app doesn't look hung during model latency.
    /// Cleared by <see cref="ApplyMessageEvent"/> on the first agent-origin
    /// event, by <see cref="LoadAsync"/> on (re)load, and on send failure.
    /// </summary>
    [ObservableProperty]
    private bool _isAgentWorking;

    /// <summary>
    /// Threads in the pivot strip. Until multi-thread conversations land we
    /// expose a single thread per workspace; the strip still renders so the
    /// design's affordance is in place.
    /// </summary>
    public ObservableCollection<ChatThreadViewModel> Threads { get; } = new();

    [ObservableProperty]
    private ChatThreadViewModel? _activeThread;

    /// <summary>Projected message timeline driving the transcript ItemsRepeater.</summary>
    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    // -------- Public lifecycle --------

    /// <summary>
    /// Load the conversation for <paramref name="id"/>, hydrate
    /// <see cref="Messages"/>, and start the live event subscription. Safe to
    /// call repeatedly with different IDs — the previous subscription is
    /// cancelled before the new one starts.
    /// </summary>
    public async Task LoadAsync(WorkspaceId id, CancellationToken ct = default)
    {
        await StopObservingAsync().ConfigureAwait(false);

        WorkspaceId = id;
        IsLoading = true;
        IsAgentWorking = false;
        Messages.Clear();
        _pendingUserEchoes.Clear();
        _currentGroup = null;
        _currentResult = null;
        Threads.Clear();
        ActiveThread = null;
        _conversationId = null;

        // Resolve the workspace first so we know which agent model to show on
        // the composer + which branch the terminal panel renders.
        var workspaceResult = await _services.Workspaces.GetAsync(id, ct).ConfigureAwait(true);
        if (workspaceResult.IsSuccess)
        {
            var settings = await _services.Settings.GetAsync(ct).ConfigureAwait(true);
            var catalog = await _services.ModelCatalog.ListModelsAsync(ct).ConfigureAwait(true);
            if (catalog.IsSuccess)
            {
                ModelBar.SetCatalog(catalog.Value);
            }
            ModelBar.SetSelections(
                workspaceResult.Value.Agent,
                settings.ReasoningEffort,
                settings.ContextTier
            );
            Terminal.Load(workspaceResult.Value);
        }
        else
        {
            // Without this, the terminal would keep showing the previous
            // workspace's prompt — the right pane would look stale rather
            // than empty.
            Terminal.Reset();
        }

        // Load + render the conversation transcript first. It's a fast local
        // SQLite read, whereas the PR pane below hits the network (PR lookup)
        // and git (workspace diff). Rendering the chat before awaiting the PR
        // pane means the transcript shows ~instantly instead of being blocked
        // behind the slow right-pane load.
        var result = await _services
            .Conversations.GetForWorkspaceAsync(id, ct)
            .ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Title = string.Empty;
            IsLoading = false;
            // Still populate the right pane so it doesn't show stale state.
            await PrPane.LoadAsync(id, ct).ConfigureAwait(true);
            return;
        }

        var conversation = result.Value;
        _conversationId = conversation.Id;
        Title = conversation.Title;

        foreach (var ev in conversation.Messages)
        {
            var titleChange = TitleChangeOf(ev);
            if (titleChange is not null)
            {
                Title = titleChange;
            }
            else
            {
                AppendProjected(MessageViewModel.FromEvent(ev));
            }
        }
        // A loaded conversation isn't actively running, so settle the last turn's
        // activity block (drop its "working" spinner).
        FinalizeCurrentTurn();

        // Single thread strip for now — design's pivot shows one active thread.
        // Its label mirrors the conversation Title so the Haiku auto-rename is
        // visible in the UI (kept in sync by OnTitleChanged).
        var thread = new ChatThreadViewModel(
            title: Title,
            icon: "",
            messageCount: conversation.Messages.Length
        );
        Threads.Add(thread);
        ActiveThread = thread;

        IsLoading = false;

        StartObserving(conversation.Id);

        // Live git status for the worktree (clean/dirty + ahead/behind).
        // Best-effort: a failure (e.g. worktree missing) just clears the line.
        var statusResult = await _services.Workspaces.GetStatusAsync(id, ct).ConfigureAwait(true);
        GitStatusSummary = statusResult.IsSuccess ? FormatStatus(statusResult.Value) : string.Empty;

        // Right pane loads after the chat is on screen — it's the slow part
        // (PR lookup over the network + git diff), so we never block the
        // transcript on it.
        await PrPane.LoadAsync(id, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Cooperative cancel of the live subscription. Synchronous so that
    /// navigation handlers (which can't be <c>async void</c> safely) can call
    /// it without ceremony. The background read task drains via the channel's
    /// cancellation registration; we don't await it. Next <see cref="LoadAsync"/>
    /// invocation will await any straggling completion via <see cref="StopObservingAsync"/>.
    /// </summary>
    public void StopObserving()
    {
        _observeCts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await StopObservingAsync().ConfigureAwait(false);
    }

    // -------- Commands --------

    private bool CanSendMessage() =>
        !string.IsNullOrWhiteSpace(ComposerText) && _conversationId is not null;

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessage(CancellationToken ct)
    {
        if (_conversationId is null)
        {
            return;
        }
        var text = ComposerText.Trim();
        if (text.Length == 0)
        {
            return;
        }
        // Clear the composer and surface the message immediately so the
        // transcript updates the instant the user hits Send — independent of
        // backend latency. The live ObserveMessages echo of this same message
        // is suppressed in ApplyMessageEvent via _pendingUserEchoes. Flip the
        // working indicator on now so the gap before the agent's first event
        // doesn't read as a hang.
        ComposerText = string.Empty;
        var optimistic = new UserMessageViewModel(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            text,
            Array.Empty<string>()
        );
        _pendingUserEchoes.Add(optimistic);
        AppendProjected(optimistic);
        IsAgentWorking = true;

        // ConfigureAwait(true): the rollback below touches UI-thread state, so
        // resume on the captured context. The stub completes synchronously; the
        // real backend resumes on the WinUI dispatcher.
        var result = await _services
            .Conversations.PostUserMessageAsync(_conversationId, text, Array.Empty<string>(), ct)
            .ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            // The post never reached the agent, so no echo will arrive — undo
            // the optimistic message and stop the spinner.
            _pendingUserEchoes.Remove(optimistic);
            Messages.Remove(optimistic);
            IsAgentWorking = false;
        }
    }

    // -------- Subscription lifecycle --------

    /// <summary>
    /// Re-fetch the workspace's changed-file list after agent activity so the
    /// "Changes" view tracks edits live. Coalesced via a pending flag so a busy
    /// agent stream doesn't queue a git diff per message.
    /// </summary>
    private void ScheduleDiffRefresh()
    {
        var wsId = WorkspaceId;
        if (wsId is null || _diffRefreshPending)
        {
            return;
        }
        _diffRefreshPending = true;
        _dispatcher.Post(async () =>
        {
            try
            {
                await PrPane.RefreshFilesAsync(wsId).ConfigureAwait(true);
            }
            finally
            {
                _diffRefreshPending = false;
            }
        });
    }

    private void StartObserving(ConversationId conversationId)
    {
        _observeCts = new CancellationTokenSource();
        var token = _observeCts.Token;

        // Run the observe loop inline (no Task.Run): the IAsyncEnumerable yields
        // at each MoveNextAsync, so the loop is non-blocking on the UI thread.
        // Avoiding Task.Run also closes a race where the threadpool task might
        // not have reached its first MoveNextAsync by the time a synchronous
        // caller posts the next event — the stub channel uses
        // AllowSynchronousContinuations=true so it depends on a pending consumer.
        // This is the inverse side of the threading contract documented on
        // IConversationService.ObserveMessages — implementations must not
        // block the call thread; if they need to, they hop to a worker on
        // their side.
        _observeTask = ObserveLoopAsync(conversationId, token);
    }

    private async Task ObserveLoopAsync(ConversationId conversationId, CancellationToken token)
    {
        try
        {
            await foreach (
                var update in _services
                    .Conversations.ObserveMessages(conversationId, token)
                    .ConfigureAwait(false)
            )
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                // TitleChanged renames the conversation (e.g. the Haiku
                // auto-rename) — apply to Title rather than the transcript.
                var titleChange = update.Match<string?>(
                    onMessage: TitleChangeOf,
                    onTurnCompleted: () => null
                );
                if (titleChange is not null)
                {
                    _dispatcher.Post(() => Title = titleChange);
                }
                else
                {
                    _dispatcher.Post(() => ApplyUpdate(update));
                    // The agent likely touched the worktree — refresh the
                    // Changes list so edits show up live.
                    ScheduleDiffRefresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on page navigation / dispose.
        }
        catch (Exception ex)
        {
            // Anything else — log so the unobserved-task GC finalizer
            // doesn't fail-fast the process. Real backends (Chunk 10)
            // will surface network / auth errors here.
            System.Diagnostics.Debug.WriteLine(
                $"[WorkspaceViewModel] ObserveMessages failed: {ex}"
            );
        }
    }

    /// <summary>
    /// Apply one live conversation update on the UI thread (posted via
    /// <see cref="_dispatcher"/>). A <c>TurnCompleted</c> marker settles the
    /// in-flight turn; otherwise the wrapped transcript event is projected.
    /// </summary>
    private void ApplyUpdate(ConversationUpdate update)
    {
        if (update.IsTurnCompleted)
        {
            OnTurnCompleted();
            return;
        }
        ApplyMessageEvent(update.Message);
    }

    /// <summary>
    /// The agent finished the current turn. Stop the "working" affordances and
    /// finalize the turn's activity group, then clear the trackers so the next
    /// turn starts fresh. Driven by the backend's explicit turn signal rather
    /// than inferred from message content.
    /// </summary>
    private void OnTurnCompleted()
    {
        IsAgentWorking = false;
        FinalizeCurrentTurn();
        _currentGroup = null;
        _currentResult = null;
        // Surface an OS notification so the user knows the agent is done even
        // when the app isn't focused. The service suppresses it when the app
        // is already in the foreground.
        _notifications.NotifyTurnCompleted(Title);
    }

    /// <summary>
    /// Apply one live <see cref="MessageEvent"/> to the transcript. Runs on the
    /// UI thread, so access to <see cref="Messages"/> and
    /// <see cref="_pendingUserEchoes"/> is serialized. Suppresses the echo of a
    /// message the user just sent (already shown optimistically).
    /// </summary>
    private void ApplyMessageEvent(MessageEvent ev)
    {
        var vm = MessageViewModel.FromEvent(ev);

        if (
            vm is UserMessageViewModel user
            && _pendingUserEchoes.Count > 0
            && _pendingUserEchoes[0].Text == user.Text
        )
        {
            // Server echo of an optimistically-shown user message — drop it so
            // the message isn't duplicated, keeping the instance already on screen.
            _pendingUserEchoes.RemoveAt(0);
            return;
        }

        AppendProjected(vm);
    }

    /// <summary>
    /// Route a projected message VM into the transcript, collapsing a turn's
    /// intermediate activity into a single grey group and surfacing only its
    /// latest result. Runs on the UI thread (hydration, optimistic send, and the
    /// dispatched live echo all call it), so the grouping trackers need no lock.
    /// </summary>
    private void AppendProjected(MessageViewModel vm)
    {
        switch (vm)
        {
            case UserMessageViewModel:
                // New turn: settle the previous turn, then surface the user line.
                FinalizeCurrentTurn();
                _currentGroup = null;
                _currentResult = null;
                Messages.Add(vm);
                break;

            case AgentMessageViewModel
            or AgentMarkdownViewModel
            or AgentErrorViewModel:
                // Result candidate. Demote any prior result into the group (it's
                // no longer the turn's last word), then surface this one. The
                // group stays "active" until the turn's explicit completion.
                if (_currentResult is not null)
                {
                    DemoteResultIntoGroup();
                }
                _currentResult = vm;
                Messages.Add(vm);
                break;

            default:
                // Activity (tool batch / change note): always collapses. Any
                // surfaced result is demoted first since activity now trails it.
                if (_currentResult is not null)
                {
                    DemoteResultIntoGroup();
                }
                EnsureGroup();
                _currentGroup!.Add(vm);
                break;
        }
    }

    private void EnsureGroup()
    {
        if (_currentGroup is null)
        {
            _currentGroup = new AgentActivityGroupViewModel();
            Messages.Add(_currentGroup);
        }
    }

    private void DemoteResultIntoGroup()
    {
        if (_currentResult is null)
        {
            return;
        }
        Messages.Remove(_currentResult);
        EnsureGroup();
        _currentGroup!.Add(_currentResult);
        _currentResult = null;
    }

    private void FinalizeCurrentTurn()
    {
        if (_currentGroup is not null)
        {
            _currentGroup.IsActive = false;
        }
    }

    /// <summary>
    /// Render a <see cref="WorktreeStatus"/> as a compact one-line summary:
    /// "Clean" when nothing is pending, else "&lt;n&gt; changed" with an
    /// "↑ahead ↓behind" suffix when the branch diverges from upstream.
    /// </summary>
    private static string FormatStatus(WorktreeStatus status)
    {
        var parts = new System.Collections.Generic.List<string>();
        var changed = status.Files.Count;
        parts.Add(changed == 0 ? "Clean" : $"{changed} changed");

        var ab = status.AheadBehind;
        if (ab.Ahead > 0 || ab.Behind > 0)
        {
            parts.Add($"↑{ab.Ahead} ↓{ab.Behind}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Keep the pivot strip's active-thread label in step with the conversation
    /// <see cref="Title"/>. This is what makes the Haiku auto-rename visible:
    /// the rename arrives as a <c>TitleChanged</c> update, sets <see cref="Title"/>,
    /// and the single thread re-labels live. Generated by the MVVM toolkit's
    /// <c>[ObservableProperty]</c> partial hook.
    /// </summary>
    partial void OnTitleChanged(string value)
    {
        if (ActiveThread is not null)
        {
            ActiveThread.Title = value;
        }
    }

    /// <summary>
    /// Returns the new title when <paramref name="ev"/> is a
    /// <c>TitleChanged</c> rename, else <c>null</c>. Goes through the F#
    /// <c>Match</c> visitor so adding an event kind forces a decision here.
    /// </summary>
    private static string? TitleChangeOf(MessageEvent ev) =>
        ev.Match<string?>(
            onUser: _ => null,
            onAgent: _ => null,
            onError: _ => null,
            onTool: _ => null,
            onChange: _ => null,
            onMarkdown: _ => null,
            onTitleChanged: t => t
        );

    private async Task StopObservingAsync()
    {
        if (_observeCts is null)
        {
            return;
        }
        _observeCts.Cancel();
        try
        {
            if (_observeTask is not null)
            {
                await _observeTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        _observeCts.Dispose();
        _observeCts = null;
        _observeTask = null;
    }
}

/// <summary>
/// One entry in the chat pivot strip — pre-Chunk-3.5 the workspace exposes a
/// single thread, but the strip itself ships now so the layout is real.
/// </summary>
public partial class ChatThreadViewModel : ObservableObject
{
    public ChatThreadViewModel(string title, string icon, int messageCount)
    {
        _title = title;
        _icon = icon;
        _messageCount = messageCount;
    }

    [ObservableProperty]
    private string _title;

    /// <summary>Segoe Fluent Icons glyph (PUA codepoint string, e.g. "").</summary>
    [ObservableProperty]
    private string _icon;

    [ObservableProperty]
    private int _messageCount;
}
