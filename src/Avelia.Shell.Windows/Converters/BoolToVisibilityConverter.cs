using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// <c>Visible</c> when the bound bool is <c>true</c>, <c>Collapsed</c> otherwise.
/// Used (e.g.) for the Settings side-nav active-bar — the bar is always rendered
/// with its accent <c>ThemeResource</c> background so the framework owns theme
/// tracking; this converter just toggles visibility.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("BoolToVisibilityConverter is one-way.");
}
