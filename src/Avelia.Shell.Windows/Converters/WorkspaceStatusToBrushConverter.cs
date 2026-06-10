using System;
using Avelia.Core.Abstractions;
using Microsoft.UI;
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
            status.IsReady ? "AveliaSuccessBrush"
            : status.IsConflict ? "AveliaWarningBrush"
            : status.IsOpen ? "AveliaInfoBrush"
            : status.IsActive ? "AveliaAccentDefaultBrush"
            : "AveliaTextTertiaryBrush"; // Draft, Archived fall through
        return ResolveBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("WorkspaceStatusToBrushConverter is one-way.");

    /// <summary>
    /// Resolve a brush resource. The Avelia palette lives in
    /// <c>ThemeDictionaries</c>, which the plain <c>Resources[key]</c> indexer
    /// does NOT search — so a naive lookup returns <c>null</c> and the dot
    /// renders with no fill (reading as black over the dark surface). This
    /// walks the top-level dictionary, then the active theme's dictionary, then
    /// any theme dictionary, before falling back to a visible gray.
    /// </summary>
    private static Brush ResolveBrush(string key)
    {
        var resources = Application.Current.Resources;

        if (resources.TryGetValue(key, out var top) && top is Brush topBrush)
        {
            return topBrush;
        }

        var themeName =
            Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Default";

        var themeDictionaries = resources.ThemeDictionaries;
        if (
            themeDictionaries.TryGetValue(themeName, out var activeObj)
            && activeObj is ResourceDictionary active
            && active.TryGetValue(key, out var activeBrushObj)
            && activeBrushObj is Brush activeBrush
        )
        {
            return activeBrush;
        }

        foreach (var entry in themeDictionaries)
        {
            if (
                entry.Value is ResourceDictionary dict
                && dict.TryGetValue(key, out var anyObj)
                && anyObj is Brush anyBrush
            )
            {
                return anyBrush;
            }
        }

        return new SolidColorBrush(Colors.Gray);
    }
}
