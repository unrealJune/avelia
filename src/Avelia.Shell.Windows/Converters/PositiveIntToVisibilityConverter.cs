using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// <c>Visible</c> when the bound int is &gt; 0, <c>Collapsed</c> otherwise.
/// Used to hide <c>+0</c> / <c>-0</c> diff chips when there are no additions
/// or deletions on a file.
/// </summary>
public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("PositiveIntToVisibilityConverter is one-way.");
}
