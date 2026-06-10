using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// Builds a <see cref="BitmapImage"/> from an absolute file path so composer
/// attachments (and transcript image refs) can render a thumbnail without the
/// view-model holding a UI-thread <c>ImageSource</c>. Returns <c>null</c> for a
/// blank path so an <c>Image</c> simply renders nothing.
/// </summary>
public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new BitmapImage(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("PathToImageSourceConverter is one-way.");
}
