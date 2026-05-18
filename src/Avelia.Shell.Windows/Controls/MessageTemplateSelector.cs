using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Picks the conversation-line DataTemplate by VM runtime type. One template
/// per concrete <see cref="MessageViewModel"/> subclass, matching the design's
/// six transcript shapes (agent, user, agent-error, tool-batch, change-note,
/// agent-md).
/// </summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AgentTemplate { get; set; }

    public DataTemplate? AgentErrorTemplate { get; set; }

    public DataTemplate? ToolBatchTemplate { get; set; }

    public DataTemplate? ChangeNoteTemplate { get; set; }

    public DataTemplate? AgentMarkdownTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item switch
        {
            UserMessageViewModel => UserTemplate,
            AgentMessageViewModel => AgentTemplate,
            AgentErrorViewModel => AgentErrorTemplate,
            ToolBatchViewModel => ToolBatchTemplate,
            ChangeNoteViewModel => ChangeNoteTemplate,
            AgentMarkdownViewModel => AgentMarkdownTemplate,
            _ => null,
        };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
