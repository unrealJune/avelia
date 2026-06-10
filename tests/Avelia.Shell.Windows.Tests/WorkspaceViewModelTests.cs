using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Conversation = Avelia.Core.Abstractions.Conversation;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class WorkspaceViewModelTests
{
    private static WorkspaceViewModel MakeVm() =>
        new(Composition.buildStubServices(), new ImmediateUiDispatcher());

    /// <summary>
    /// Clone the stub service graph but swap in a custom conversation service so
    /// a test can drive arbitrary agent events into the observe stream.
    /// </summary>
    private static AveliaServices ServicesWith(IConversationService conversations)
    {
        var s = Composition.buildStubServices();
        return new AveliaServices(
            s.Repositories,
            s.Workspaces,
            conversations,
            s.Diffs,
            s.PullRequests,
            s.Runs,
            s.Inbox,
            s.Settings,
            s.ModelCatalog,
            s.Agents,
            s.Terminals
        );
    }

    [Fact]
    public async Task LoadAsync_GroupsTurnsAndSurfacesFinalResult()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Equal(DesignData.archiveConversation.Title, vm.Title);

        // The 8 raw events collapse into:
        //   [activity group] [user message] [activity group] [final markdown]
        Assert.Collection(
            vm.Messages,
            m => Assert.IsType<AgentActivityGroupViewModel>(m),
            m => Assert.IsType<UserMessageViewModel>(m),
            m => Assert.IsType<AgentActivityGroupViewModel>(m),
            m => Assert.IsType<AgentMarkdownViewModel>(m)
        );

        // Only the turn's end result stays surfaced.
        Assert.IsType<AgentMarkdownViewModel>(vm.Messages.Last());
    }

    [Fact]
    public async Task LoadAsync_CollapsesIntermediateEventsIntoMinimizedGroups()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var groups = vm.Messages.OfType<AgentActivityGroupViewModel>().ToList();
        Assert.Equal(2, groups.Count);

        // Minimized by default; a loaded conversation isn't actively working.
        Assert.All(groups, g => Assert.False(g.IsExpanded));
        Assert.All(groups, g => Assert.False(g.IsActive));

        // Every collapsed kind ends up inside a group — including an agent prose
        // message that was superseded by a later result and so demoted.
        var collapsed = groups.SelectMany(g => g.Items).ToList();
        Assert.Contains(collapsed, m => m is ToolBatchViewModel);
        Assert.Contains(collapsed, m => m is ChangeNoteViewModel);
        Assert.Contains(collapsed, m => m is AgentErrorViewModel);
        Assert.Contains(collapsed, m => m is AgentMessageViewModel);

        // The summary line reflects the collapsed steps.
        Assert.Contains("tool", groups[0].Summary);
    }

    [Fact]
    public async Task LoadAsync_SeedsSingleThreadInPivot()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Single(vm.Threads);
        Assert.NotNull(vm.ActiveThread);
        Assert.Equal("Main", vm.ActiveThread!.Title);
    }

    [Fact]
    public async Task SendMessage_BlankComposer_IsDisabled()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendMessage_WithComposerText_PostsToConversationAndAppendsMessage()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        var initialCount = vm.Messages.Count;
        vm.ComposerText = "hello agent";

        Assert.True(vm.SendMessageCommand.CanExecute(null));
        await vm.SendMessageCommand.ExecuteAsync(null);

        // The message is shown optimistically and the live echo for the same
        // send is suppressed, so exactly one message is appended (not two).
        Assert.Equal(string.Empty, vm.ComposerText);
        Assert.Equal(initialCount + 1, vm.Messages.Count);
        var appended = vm.Messages.Last();
        var user = Assert.IsType<UserMessageViewModel>(appended);
        Assert.Equal("hello agent", user.Text);
    }

    [Fact]
    public async Task SendMessage_WithAttachments_ThreadsRefsThroughAndClearsThem()
    {
        var fake = new FakeConversationService(
            FakeConversationService.EmptyFor(DesignData.archiveWorkspaceId)
        );
        var vm = new WorkspaceViewModel(ServicesWith(fake), new ImmediateUiDispatcher());
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        vm.ComposerText = "look at this";
        vm.Attachments.Add(new ComposerAttachmentViewModel(@"C:\tmp\paste-1.png"));
        vm.Attachments.Add(new ComposerAttachmentViewModel(@"C:\tmp\paste-2.png"));

        await vm.SendMessageCommand.ExecuteAsync(null);

        // The attachment paths are posted as refs and surfaced on the optimistic
        // message, then the staging area is cleared.
        Assert.Equal(new[] { @"C:\tmp\paste-1.png", @"C:\tmp\paste-2.png" }, fake.LastRefs);
        var user = Assert.IsType<UserMessageViewModel>(vm.Messages.Last());
        Assert.Equal(new[] { @"C:\tmp\paste-1.png", @"C:\tmp\paste-2.png" }, user.Refs);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task SendMessage_AttachmentOnlyWithNoText_IsEnabledAndSends()
    {
        var fake = new FakeConversationService(
            FakeConversationService.EmptyFor(DesignData.archiveWorkspaceId)
        );
        var vm = new WorkspaceViewModel(ServicesWith(fake), new ImmediateUiDispatcher());
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // No text — staging an image alone must still enable Send.
        Assert.False(vm.SendMessageCommand.CanExecute(null));
        vm.Attachments.Add(new ComposerAttachmentViewModel(@"C:\tmp\paste.png"));
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\tmp\paste.png" }, fake.LastRefs);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task SendMessage_TurnsOnTheWorkingIndicator()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.False(vm.IsAgentWorking);
        vm.ComposerText = "hello agent";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // No agent reply arrives on the stub path, so the indicator stays on
        // until a real agent event would clear it.
        Assert.True(vm.IsAgentWorking);
    }

    [Fact]
    public async Task TurnCompleted_ClearsWorkingIndicator()
    {
        var fake = new FakeConversationService(DesignData.archiveConversation);
        var vm = new WorkspaceViewModel(ServicesWith(fake), new ImmediateUiDispatcher());
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        vm.ComposerText = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);
        Assert.True(vm.IsAgentWorking);

        // An agent reply mid-turn does NOT end the turn — the indicator stays on.
        fake.PushAgentMessage("on it");
        Assert.True(vm.IsAgentWorking);
        Assert.Equal("on it", Assert.IsType<AgentMessageViewModel>(vm.Messages.Last()).Text);

        // The explicit turn-completed signal stops it.
        fake.PushTurnCompleted();
        Assert.False(vm.IsAgentWorking);
    }

    [Fact]
    public async Task LoadAsync_ResetsWorkingIndicator()
    {
        var fake = new FakeConversationService(DesignData.archiveConversation);
        var vm = new WorkspaceViewModel(ServicesWith(fake), new ImmediateUiDispatcher());
        await vm.LoadAsync(DesignData.archiveWorkspaceId);
        vm.ComposerText = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);
        Assert.True(vm.IsAgentWorking);

        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.False(vm.IsAgentWorking);
    }

    [Fact]
    public async Task LiveTurn_CollapsesActivityAndDemotesSupersededResult()
    {
        var fake = new FakeConversationService(
            FakeConversationService.EmptyFor(DesignData.archiveWorkspaceId)
        );
        var vm = new WorkspaceViewModel(ServicesWith(fake), new ImmediateUiDispatcher());
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        vm.ComposerText = "go";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // First reply is surfaced directly (no activity yet → no group).
        fake.PushAgentMessage("first pass");
        Assert.IsType<AgentMessageViewModel>(vm.Messages.Last());
        Assert.DoesNotContain(vm.Messages, m => m is AgentActivityGroupViewModel);

        // A tool call arrives after that reply → the reply is demoted into a new
        // grey group. The group is "active" while the turn is in progress.
        fake.PushToolBatch(3);
        var group = Assert.IsType<AgentActivityGroupViewModel>(vm.Messages.Last());
        Assert.True(group.IsActive);
        Assert.Contains(group.Items, m => m is AgentMessageViewModel um && um.Text == "first pass");
        Assert.Contains(group.Items, m => m is ToolBatchViewModel);

        // The final reply is surfaced after the group; the turn is still open
        // until the backend signals completion, so the group stays active.
        fake.PushAgentMessage("all done");
        Assert.Equal("all done", Assert.IsType<AgentMessageViewModel>(vm.Messages.Last()).Text);
        Assert.True(group.IsActive);

        // The turn-completed signal settles the group (drops its spinner).
        fake.PushTurnCompleted();
        Assert.False(group.IsActive);
        Assert.False(vm.IsAgentWorking);
        Assert.Same(group, vm.Messages.OfType<AgentActivityGroupViewModel>().Single());
        Assert.Equal("all done", Assert.IsType<AgentMessageViewModel>(vm.Messages.Last()).Text);
    }

    [Fact]
    public async Task TurnCompleted_RaisesOsNotificationWithConversationTitle()
    {
        var fake = new FakeConversationService(DesignData.archiveConversation);
        var notifications = new RecordingNotificationService();
        var vm = new WorkspaceViewModel(
            ServicesWith(fake),
            new ImmediateUiDispatcher(),
            notifications
        );
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        vm.ComposerText = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Mid-turn agent activity must not raise a notification.
        fake.PushAgentMessage("on it");
        Assert.Empty(notifications.Notified);

        // The turn-completed signal raises exactly one notification, carrying
        // the conversation title.
        fake.PushTurnCompleted();
        Assert.Equal(new[] { vm.Title }, notifications.Notified);
    }

    [Fact]
    public async Task LoadAsync_DoesNotRaiseNotificationForHydratedHistory()
    {
        var notifications = new RecordingNotificationService();
        var vm = new WorkspaceViewModel(
            Composition.buildStubServices(),
            new ImmediateUiDispatcher(),
            notifications
        );

        // Replaying a finished conversation's transcript must stay silent — only
        // a live turn-completed signal notifies.
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Empty(notifications.Notified);
    }

    [Fact]
    public async Task LoadAsync_AlsoLoadsPrPaneAndTerminal()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // Right-pane composite is hydrated alongside the conversation.
        Assert.True(vm.PrPane.HasPullRequest);
        Assert.Equal(DesignData.archivePullRequest.Number, vm.PrPane.Number);
        Assert.Equal(DesignData.diffFiles.Count, vm.PrPane.Files.Count);

        // Terminal panel reflects the active workspace's branch + base.
        var workspace = DesignData.workspaces.First(w =>
            w.Id.Equals(DesignData.archiveWorkspaceId)
        );
        Assert.Equal(workspace.Branch.Value, vm.Terminal.Branch);
        Assert.Equal(workspace.Base.Value, vm.Terminal.Base);
    }

    [Fact]
    public async Task LoadAsync_UnknownWorkspace_ClearsTerminal()
    {
        var vm = MakeVm();
        // Hydrate with a real workspace first so we can verify the reset path
        // actually clears existing state (not just "stayed empty").
        await vm.LoadAsync(DesignData.archiveWorkspaceId);
        Assert.NotEqual(string.Empty, vm.Terminal.PromptLine);
        Assert.NotNull(vm.ModelBar.SelectedModel);

        var bogus = WorkspaceId.NewWorkspaceId(System.Guid.NewGuid());
        await vm.LoadAsync(bogus);

        Assert.Equal(string.Empty, vm.Terminal.PromptLine);
        Assert.Equal(string.Empty, vm.Terminal.Branch);
        Assert.Equal(string.Empty, vm.Terminal.Base);
        Assert.False(vm.PrPane.HasPullRequest);
        Assert.Empty(vm.PrPane.Files);
    }

    [Fact]
    public async Task LoadAsync_ResettingToSameWorkspace_StartsFreshTranscript()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);
        var initial = vm.Messages.Count;

        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Equal(initial, vm.Messages.Count);
    }

    [Fact]
    public async Task DisposeAsync_CompletesObserveStream()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // Disposing must complete cleanly without leaking the observer task.
        await vm.DisposeAsync();
    }
}

public class MessageViewModelTests
{
    [Fact]
    public void FromEvent_RoundtripsAllSixDesignEventKinds()
    {
        // Project every event in the seeded conversation; exhaustiveness on the
        // F# Match side guarantees no event kind falls through.
        var conv = DesignData.archiveConversation;
        var projected = conv.Messages.Select(MessageViewModel.FromEvent).ToList();

        Assert.Equal(conv.Messages.Length, projected.Count);
        Assert.All(projected, vm => Assert.NotEqual(System.Guid.Empty, vm.Id));
    }
}

/// <summary>
/// Test double for <see cref="IConversationService"/> that echoes user messages
/// (like the real service) and lets a test push arbitrary agent events into the
/// live observe stream. Channels use <c>AllowSynchronousContinuations</c> so,
/// paired with <see cref="ImmediateUiDispatcher"/>, pushes are observed
/// synchronously on the calling thread.
/// </summary>
internal sealed class FakeConversationService : IConversationService
{
    private readonly Conversation _conversation;
    private readonly object _gate = new();
    private readonly List<Channel<ConversationUpdate>> _subscribers = new();

    public FakeConversationService(Conversation conversation) => _conversation = conversation;

    /// <summary>An empty conversation for the given workspace — handy for driving
    /// a turn from scratch without seeded history.</summary>
    public static Conversation EmptyFor(WorkspaceId workspaceId) =>
        new(
            ConversationId.NewConversationId(Guid.NewGuid()),
            workspaceId,
            "Test",
            Array.Empty<MessageEvent>(),
            0
        );

    public System.Threading.Tasks.Task<OperationResult<Conversation>> GetForWorkspaceAsync(
        WorkspaceId workspaceId,
        CancellationToken ct
    ) => Task.FromResult(OperationResult<Conversation>.NewSuccess(_conversation));

    public System.Threading.Tasks.Task<OperationResult<UserMessage>> PostUserMessageAsync(
        ConversationId conversationId,
        string text,
        string[] refs,
        CancellationToken ct
    )
    {
        LastRefs = refs;
        var msg = new UserMessage(
            MessageId.NewMessageId(Guid.NewGuid()),
            text,
            refs,
            DateTimeOffset.Now
        );
        Broadcast(MessageEvent.NewUserMessageAppended(msg));
        return Task.FromResult(OperationResult<UserMessage>.NewSuccess(msg));
    }

    /// <summary>Refs supplied to the most recent <see cref="PostUserMessageAsync"/> call.</summary>
    public string[]? LastRefs { get; private set; }

    public IAsyncEnumerable<ConversationUpdate> ObserveMessages(
        ConversationId conversationId,
        CancellationToken ct
    )
    {
        var channel = Channel.CreateUnbounded<ConversationUpdate>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                AllowSynchronousContinuations = true,
            }
        );
        lock (_gate)
        {
            _subscribers.Add(channel);
        }
        ct.Register(() =>
        {
            channel.Writer.TryComplete();
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        });
        return channel.Reader.ReadAllAsync(ct);
    }

    public void PushAgentMessage(string text)
    {
        var msg = new AgentMessage(
            MessageId.NewMessageId(Guid.NewGuid()),
            text,
            DateTimeOffset.Now
        );
        Broadcast(MessageEvent.NewAgentMessageAppended(msg));
    }

    public void PushToolBatch(int toolCount)
    {
        var batch = new ToolBatch(
            MessageId.NewMessageId(Guid.NewGuid()),
            toolCount,
            0,
            Array.Empty<string>(),
            DateTimeOffset.Now
        );
        Broadcast(MessageEvent.NewToolBatchAppended(batch));
    }

    /// <summary>Signal the end of the agent's turn (what the real service emits
    /// when the headless session's send completes).</summary>
    public void PushTurnCompleted() => Broadcast(ConversationUpdate.TurnCompleted);

    private void Broadcast(MessageEvent ev) => Broadcast(ConversationUpdate.NewMessageAppended(ev));

    private void Broadcast(ConversationUpdate update)
    {
        Channel<ConversationUpdate>[] snapshot;
        lock (_gate)
        {
            snapshot = _subscribers.ToArray();
        }
        foreach (var ch in snapshot)
        {
            ch.Writer.TryWrite(update);
        }
    }
}

/// <summary>
/// Recording <see cref="INotificationService"/> that captures the conversation
/// titles passed to <see cref="NotifyTurnCompleted"/> so tests can assert on
/// when (and with what) a turn-complete notification was raised.
/// </summary>
internal sealed class RecordingNotificationService : INotificationService
{
    public List<string> Notified { get; } = new();

    public void NotifyTurnCompleted(string conversationTitle) => Notified.Add(conversationTitle);
}
