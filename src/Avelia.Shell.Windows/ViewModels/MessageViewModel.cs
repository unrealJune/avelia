using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avelia.Core.Abstractions;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Base type for one entry in the conversation transcript. Each <see cref="MessageEvent"/>
/// case projects to a concrete subclass; the page's <c>MessageTemplateSelector</c>
/// picks a XAML template by runtime type. Keeping the projection logic here (a
/// single <see cref="FromEvent"/> switch) makes the mapping testable without a
/// WinUI host.
///
/// Message VMs are immutable DTOs — no INPC overhead. If/when streaming
/// agent text lands, that'll likely be a sibling <c>AgentMessageStreamingViewModel</c>
/// that *does* observe, rather than mutating an existing instance.
/// </summary>
public abstract class MessageViewModel
{
    protected MessageViewModel(Guid id, DateTimeOffset timestamp)
    {
        Id = id;
        Timestamp = timestamp;
    }

    public Guid Id { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Project an F#-side <see cref="MessageEvent"/> to its concrete VM. Goes
    /// through <c>MessageEvent.Match</c> so the F# compiler enforces
    /// exhaustiveness — adding a new event case breaks the build here.
    /// </summary>
    public static MessageViewModel FromEvent(MessageEvent ev) =>
        ev.Match<MessageViewModel>(
            onUser: UserMessageViewModel.From,
            onAgent: AgentMessageViewModel.From,
            onError: AgentErrorViewModel.From,
            onTool: ToolBatchViewModel.From,
            onChange: ChangeNoteViewModel.From,
            onMarkdown: AgentMarkdownViewModel.From,
            // TitleChanged is a metadata rename, not a transcript entry: it
            // updates the conversation title and is handled out-of-band by the
            // workspace VM before projection. It must never reach here.
            onTitleChanged: _ =>
                throw new InvalidOperationException(
                    "TitleChanged is handled out-of-band (it renames the conversation) and must not be projected to a transcript view-model."
                )
        );
}

// ============================================================================
//  Concrete VMs — one per MessageEvent case
// ============================================================================

/// <summary>
/// User-authored message. Carries the raw text and the list of code-refs the
/// user @-mentioned so the renderer can highlight them inline.
/// </summary>
public sealed class UserMessageViewModel : MessageViewModel
{
    public UserMessageViewModel(
        Guid id,
        DateTimeOffset timestamp,
        string text,
        IReadOnlyList<string> refs
    )
        : base(id, timestamp)
    {
        Text = text;
        Refs = refs;
    }

    public string Text { get; }

    public IReadOnlyList<string> Refs { get; }

    internal static MessageViewModel From(UserMessage m) =>
        new UserMessageViewModel(m.Id.Value, m.Timestamp, m.Text, m.Refs);
}

/// <summary>Agent reply rendered as a body block; code-refs inside are styled inline.</summary>
public sealed class AgentMessageViewModel : MessageViewModel
{
    public AgentMessageViewModel(Guid id, DateTimeOffset timestamp, string text)
        : base(id, timestamp)
    {
        Text = text;
    }

    public string Text { get; }

    internal static MessageViewModel From(AgentMessage m) =>
        new AgentMessageViewModel(m.Id.Value, m.Timestamp, m.Text);
}

/// <summary>Red error banner — agent surfaced an exception or compile failure.</summary>
public sealed class AgentErrorViewModel : MessageViewModel
{
    public AgentErrorViewModel(Guid id, DateTimeOffset timestamp, string text)
        : base(id, timestamp)
    {
        Text = text;
    }

    public string Text { get; }

    internal static MessageViewModel From(AgentErrorMessage m) =>
        new AgentErrorViewModel(m.Id.Value, m.Timestamp, m.Text);
}

/// <summary>Collapsed strip showing N tools + M messages spent on a batch of operations.</summary>
public sealed class ToolBatchViewModel : MessageViewModel
{
    public ToolBatchViewModel(
        Guid id,
        DateTimeOffset timestamp,
        int toolCount,
        int messageCount,
        IReadOnlyList<string> toolKinds
    )
        : base(id, timestamp)
    {
        ToolCount = toolCount;
        MessageCount = messageCount;
        ToolKinds = toolKinds;
        Summary =
            $"{toolCount} tool{(toolCount == 1 ? "" : "s")}, {messageCount} message{(messageCount == 1 ? "" : "s")}";
    }

    public int ToolCount { get; }

    public int MessageCount { get; }

    public IReadOnlyList<string> ToolKinds { get; }

    /// <summary>Pre-formatted summary line ("13 tools, 7 messages").</summary>
    public string Summary { get; }

    internal static MessageViewModel From(ToolBatch m) =>
        new ToolBatchViewModel(m.Id.Value, m.Timestamp, m.ToolCount, m.MessageCount, m.ToolKinds);
}

/// <summary>
/// Single-file change note ("renamed/edited @path · +N −M"). Distinguished
/// visually from a full agent message by a thin bordered card.
/// </summary>
public sealed class ChangeNoteViewModel : MessageViewModel
{
    public ChangeNoteViewModel(
        Guid id,
        DateTimeOffset timestamp,
        string filePath,
        string folder,
        string fileName,
        int add,
        int del
    )
        : base(id, timestamp)
    {
        FilePath = filePath;
        Folder = folder;
        FileName = fileName;
        Add = add;
        Del = del;
    }

    public string FilePath { get; }

    public string Folder { get; }

    public string FileName { get; }

    public int Add { get; }

    public int Del { get; }

    internal static MessageViewModel From(ChangeNote m) =>
        new ChangeNoteViewModel(
            id: m.Id.Value,
            timestamp: m.Timestamp,
            filePath: m.File.Value,
            folder: m.File.Folder,
            fileName: m.File.FileName,
            add: m.Add,
            del: m.Del
        );
}

/// <summary>
/// Agent message rendered as a heading + body + ordered list. Mirrors the
/// design's "agent-md" template (data.jsx :: summary).
/// </summary>
public sealed class AgentMarkdownViewModel : MessageViewModel
{
    public AgentMarkdownViewModel(
        Guid id,
        DateTimeOffset timestamp,
        string heading,
        string body,
        IReadOnlyList<AgentMarkdownListItem> items
    )
        : base(id, timestamp)
    {
        Heading = heading;
        Body = body;
        Items = items;
    }

    public string Heading { get; }

    public bool HasHeading => !string.IsNullOrEmpty(Heading);

    public string Body { get; }

    public IReadOnlyList<AgentMarkdownListItem> Items { get; }

    internal static MessageViewModel From(AgentMarkdown m)
    {
        var items = m.Items.Select(i => new AgentMarkdownListItem(i.Bold, i.Detail)).ToList();
        return new AgentMarkdownViewModel(m.Id.Value, m.Timestamp, m.Heading, m.Body, items);
    }
}

/// <summary>One entry in an <see cref="AgentMarkdownViewModel"/>'s ordered list.</summary>
public sealed class AgentMarkdownListItem
{
    public AgentMarkdownListItem(string bold, string detail)
    {
        Bold = bold;
        Detail = detail;
    }

    public string Bold { get; }

    public string Detail { get; }
}

/// <summary>
/// Collapses one turn's intermediate activity — tool batches, change notes, and
/// superseded agent messages — into a single grey, minimized-by-default block.
/// Only the turn's final result stays surfaced in the transcript; everything
/// that led up to it lives here, expandable on demand.
///
/// Unlike the other (immutable) message VMs this one is observable: the grouping
/// projection in <c>WorkspaceViewModel</c> appends to <see cref="Items"/> live
/// and toggles <see cref="IsActive"/> as the turn progresses, and the user can
/// flip <see cref="IsExpanded"/>. INPC is hand-rolled because the type must
/// derive from <see cref="MessageViewModel"/> (so it fits the transcript
/// collection) and can't also inherit a toolkit base.
/// </summary>
public sealed class AgentActivityGroupViewModel : MessageViewModel, INotifyPropertyChanged
{
    public AgentActivityGroupViewModel()
        : base(Guid.NewGuid(), DateTimeOffset.Now) { }

    /// <summary>The collapsed intermediate events, in arrival order.</summary>
    public ObservableCollection<MessageViewModel> Items { get; } = new();

    private bool _isExpanded;

    /// <summary>Minimized by default; the user expands to inspect the steps.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private bool _isActive = true;

    /// <summary>
    /// True while the agent is still working through this turn (activity is the
    /// trailing content and no final result has been surfaced yet). Drives a
    /// small spinner in the block header. Finalized to false once the turn ends.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    private string _summary = "Activity";

    /// <summary>Header line, e.g. <c>"13 tools · 1 edit · 2 messages"</c>.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>Append a collapsed step and refresh the summary line.</summary>
    public void Add(MessageViewModel item)
    {
        Items.Add(item);
        Summary = BuildSummary();
    }

    private string BuildSummary()
    {
        var tools = Items.OfType<ToolBatchViewModel>().Sum(t => t.ToolCount);
        var edits = Items.OfType<ChangeNoteViewModel>().Count();
        var messages = Items.Count(i =>
            i is AgentMessageViewModel or AgentMarkdownViewModel or AgentErrorViewModel
        );

        var parts = new List<string>();
        if (tools > 0)
        {
            parts.Add($"{tools} tool{(tools == 1 ? "" : "s")}");
        }
        if (edits > 0)
        {
            parts.Add($"{edits} edit{(edits == 1 ? "" : "s")}");
        }
        if (messages > 0)
        {
            parts.Add($"{messages} message{(messages == 1 ? "" : "s")}");
        }
        return parts.Count == 0 ? "Activity" : string.Join(" · ", parts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
