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
    private CancellationTokenSource? _observeCts;
    private Task? _observeTask;
    private ConversationId? _conversationId;

    public WorkspaceViewModel(AveliaServices services, IUiDispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
    }

    // -------- Observable state --------

    /// <summary>Workspace currently being viewed. Set by <see cref="LoadAsync"/>.</summary>
    [ObservableProperty]
    private WorkspaceId? _workspaceId;

    /// <summary>Conversation title (e.g. "Debugging ReferenceError"). Empty until loaded.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>
    /// Human-readable model name shown on the composer's model badge
    /// (e.g. "Sonnet 4.5"). Populated from the workspace's <c>Agent</c>
    /// choice when <see cref="LoadAsync"/> completes.
    /// </summary>
    [ObservableProperty]
    private string _modelName = string.Empty;

    /// <summary>Composer text. Bound two-way to the multi-line TextBox.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composerText = string.Empty;

    /// <summary>Indicates a load is in progress; bound to a progress ring.</summary>
    [ObservableProperty]
    private bool _isLoading;

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
        Messages.Clear();
        Threads.Clear();
        ActiveThread = null;
        _conversationId = null;

        // Resolve the workspace first so we know which agent model to show on
        // the composer. Then the conversation snapshot.
        var workspaceResult = await _services.Workspaces.GetAsync(id, ct).ConfigureAwait(true);
        ModelName = workspaceResult.IsSuccess
            ? FormatModel(workspaceResult.Value.Agent)
            : string.Empty;

        var result = await _services.Conversations.GetForWorkspaceAsync(id, ct).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Title = string.Empty;
            IsLoading = false;
            return;
        }

        var conversation = result.Value;
        _conversationId = conversation.Id;
        Title = conversation.Title;

        foreach (var ev in conversation.Messages)
        {
            Messages.Add(MessageViewModel.FromEvent(ev));
        }

        // Single thread strip for now — design's pivot shows one active thread.
        var thread = new ChatThreadViewModel(
            title: "Main",
            icon: "",
            messageCount: conversation.Messages.Length);
        Threads.Add(thread);
        ActiveThread = thread;

        IsLoading = false;

        StartObserving(conversation.Id);
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

    private bool CanSendMessage() => !string.IsNullOrWhiteSpace(ComposerText) && _conversationId is not null;

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
        // Clear composer optimistically; ObserveMessages echoes the new event.
        ComposerText = string.Empty;
        await _services.Conversations
            .PostUserMessageAsync(_conversationId, text, Array.Empty<string>(), ct)
            .ConfigureAwait(false);
    }

    // -------- Subscription lifecycle --------

    private void StartObserving(ConversationId conversationId)
    {
        _observeCts = new CancellationTokenSource();
        var token = _observeCts.Token;
        _observeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in _services.Conversations.ObserveMessages(conversationId, token).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    var vm = MessageViewModel.FromEvent(ev);
                    _dispatcher.Post(() => Messages.Add(vm));
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
                System.Diagnostics.Debug.WriteLine($"[WorkspaceViewModel] ObserveMessages failed: {ex}");
            }
        }, token);
    }

    private static string FormatModel(ModelChoice agent) =>
        agent.Match<string>(
            sonnet45: () => "Sonnet 4.5",
            opus41: () => "Opus 4.1",
            haiku45: () => "Haiku 4.5",
            custom: name => name);

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
