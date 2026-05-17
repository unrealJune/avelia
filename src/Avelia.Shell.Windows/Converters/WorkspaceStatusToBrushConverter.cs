using System;
using Avelia.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// Maps a <see cref="WorkspaceStatus"/> to one of the Avelia status brushes.
/// </summary>
public sealed class WorkspaceStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not WorkspaceStatus status)
        {
            return ResolveBrush("AveliaTextTertiaryBrush");
        }

        var key =
            status.IsReady    ? "AveliaSuccessBrush" :
            status.IsConflict ? "AveliaWarningBrush" :
            status.IsOpen     ? "AveliaInfoBrush" :
            status.IsActive   ? "AveliaAccentDefaultBrush" :
                                "AveliaTextTertiaryBrush"; // Draft, Archived fall through
        return ResolveBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("WorkspaceStatusToBrushConverter is one-way.");

    private static Brush ResolveBrush(string key) =>
        (Brush)Application.Current.Resources[key];
}
