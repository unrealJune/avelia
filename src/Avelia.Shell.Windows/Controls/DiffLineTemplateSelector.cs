using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Picks the diff-line template by <see cref="DiffLineViewModel.Kind"/>. Three
/// templates — addition / deletion / context — let the XAML side use
/// <c>{ThemeResource}</c> brushes directly instead of resolving them in code
/// (which would freeze on first paint and miss theme switches; see the
/// <c>ThemeUsageLintTests</c> for the regression class).
/// </summary>
public sealed class DiffLineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AdditionTemplate { get; set; }

    public DataTemplate? DeletionTemplate { get; set; }

    public DataTemplate? ContextTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item switch
        {
            DiffLineViewModel line when line.IsAddition => AdditionTemplate,
            DiffLineViewModel line when line.IsDeletion => DeletionTemplate,
            DiffLineViewModel => ContextTemplate,
            _ => null,
        };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
