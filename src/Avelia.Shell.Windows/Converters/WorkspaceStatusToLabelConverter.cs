using System;
using Avelia.Core.Abstractions;
using Microsoft.UI.Xaml.Data;

namespace Avelia.Shell.Windows.Converters;

/// <summary>
/// User-visible label for a <see cref="WorkspaceStatus"/>. Used in tooltips
/// and accessibility names.
/// </summary>
public sealed class WorkspaceStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not WorkspaceStatus status)
        {
            return "";
        }
        if (status.IsReady)
            return "Ready to merge";
        if (status.IsConflict)
            return "Merge conflicts";
        if (status.IsArchived)
            return "Archived";
        if (status.IsDraft)
            return "Draft";
        if (status.IsActive)
            return "Active";
        if (status.IsOpen)
            return "Open";
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("WorkspaceStatusToLabelConverter is one-way.");
}
