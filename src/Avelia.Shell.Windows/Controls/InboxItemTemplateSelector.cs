using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Picks the inbox row template by <see cref="Avelia.Core.Abstractions.InboxItemKind"/>.
/// Dispatch goes through <c>InboxItemKind.Match</c> so adding a new case to the
/// F# DU forces a compile error here instead of falling through to the default
/// template silently. One template per kind keeps the leading tile's brushes
/// inline <c>{ThemeResource}</c> references — same pattern
/// <see cref="DiffLineTemplateSelector"/> uses to dodge the brush-freezing
/// regression class guarded by <c>ThemeUsageLintTests</c>.
/// </summary>
public sealed class InboxItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? WarningTemplate { get; set; }

    public DataTemplate? SuccessTemplate { get; set; }

    public DataTemplate? InfoTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is InboxItemViewModel row
            ? row.Kind.Match<DataTemplate?>(
                onWarning: () => WarningTemplate,
                onSuccess: () => SuccessTemplate,
                onInfo: () => InfoTemplate
            )
            : null;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
