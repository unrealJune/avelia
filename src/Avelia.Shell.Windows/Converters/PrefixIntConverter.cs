using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// Formats an integer with a textual prefix supplied via <c>ConverterParameter</c>
/// (e.g. <c>"+"</c> or <c>"−"</c>). Returns the raw number with the prefix prepended.
/// </summary>
public sealed class PrefixIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
        var prefix = parameter as string ?? "";
        return $"{prefix}{n.ToString(CultureInfo.InvariantCulture)}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("PrefixIntConverter is one-way.");
}
