using System;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Helpers;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// Resolve an <see cref="AccentChoice"/> to a <see cref="SolidColorBrush"/>
/// from its CSS-style hex. Used by the Settings → Appearance swatch list,
/// where each swatch's fill must match its accent variant regardless of the
/// currently-selected accent. Cheap: one parse + brush allocation per call,
/// invoked once per swatch when the list materializes.
/// </summary>
public sealed class AccentChoiceToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is AccentChoice choice && HexColor.TryParse(choice.Hex, out var color))
        {
            return new SolidColorBrush(color);
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("AccentChoiceToBrushConverter is one-way.");
}
