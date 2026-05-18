using System;
using System.Collections.Generic;
using System.Linq;
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
            onMarkdown: AgentMarkdownViewModel.From
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
