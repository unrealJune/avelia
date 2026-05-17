using System.Linq;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.ViewModels;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.Tests;

public class WorkspaceViewModelTests
{
    private static WorkspaceViewModel MakeVm() =>
        new(Composition.buildStubServices(), new ImmediateUiDispatcher());

    [Fact]
    public async Task LoadAsync_PopulatesTitleAndMessagesFromSeededConversation()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        Assert.Equal(DesignData.archiveConversation.Title, vm.Title);
        Assert.Equal(DesignData.archiveConversation.Messages.Length, vm.Messages.Count);
    }

    [Fact]
    public async Task LoadAsync_ProjectsEachMessageEventToConcreteVm()
    {
        var vm = MakeVm();
        await vm.LoadAsync(DesignData.archiveWorkspaceId);

        // The seeded conversation has one of each kind. Confirm every concrete
        // VM type appears at least once — exhaustive projection proof.
        Assert.Contains(vm.Messages, m => m is UserMessageViewModel);
        Assert.Contains(vm.Messages, m => m is AgentMessageViewModel);
        Assert.Contains(vm.Messages, m => m is AgentErrorViewModel);
        Assert.Contains(vm.Messages, m => m is ToolBatchViewModel);
        Assert.Contains(vm.Messages, m => m is ChangeNoteViewModel);
        Assert.Contains(vm.Messages, m => m is AgentMarkdownViewModel);
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

        // Composer clears optimistically; the observe stream echoes the new event.
        Assert.Equal(string.Empty, vm.ComposerText);
        Assert.Equal(initialCount + 1, vm.Messages.Count);
        var appended = vm.Messages.Last();
        var user = Assert.IsType<UserMessageViewModel>(appended);
        Assert.Equal("hello agent", user.Text);
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
