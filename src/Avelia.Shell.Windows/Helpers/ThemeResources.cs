using Microsoft.UI.Xaml;

namespace Avelia.Shell.Windows.Helpers;

/// <summary>
/// Look up a theme-keyed resource against the host element's <em>current</em>
/// <see cref="FrameworkElement.ActualTheme"/>.
///
/// Why this helper exists: Avelia's theme tokens live in <c>Tokens.xaml</c>,
/// which is a merged dictionary holding <c>ThemeDictionaries</c>. Plain
/// <c>Application.Current.Resources[key]</c> resolves against
/// <see cref="Application.RequestedTheme"/> only — set at startup, never
/// updated, so it freezes the brush on the original theme. After a runtime
/// theme flip, that lookup hands back the wrong brush.
///
/// The walk goes through <c>Application.Current.Resources.MergedDictionaries</c>
/// (which is where merged-in token files actually live), into the matching
/// <c>ThemeDictionaries["Default" / "Light"]</c>, and finally falls back to the
/// top-level dictionary for theme-independent resources (fonts, corner radii).
/// </summary>
public static class ThemeResources
{
    /// <summary>
    /// Resolve <paramref name="key"/> against the merged theme dictionaries,
    /// preferring the variant that matches <paramref name="host"/>'s
    /// <c>ActualTheme</c>. Returns <c>null</c> if no resource with that key
    /// exists in any theme dictionary or in the top-level resources.
    /// </summary>
    public static object? Resolve(FrameworkElement host, string key)
    {
        var themeKey = host.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        return ResolveByTheme(themeKey, key);
    }

    /// <summary>
    /// Lower-level variant for callers without a host element (e.g. <c>Window</c>
    /// code-behind, which isn't a <see cref="FrameworkElement"/>). Caller passes
    /// the theme tag directly — <c>"Light"</c> or <c>"Default"</c>.
    /// </summary>
    public static object? Resolve(string themeKey, string key) => ResolveByTheme(themeKey, key);

    private static object? ResolveByTheme(string themeKey, string key)
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (
                merged.ThemeDictionaries.TryGetValue(themeKey, out var td)
                && td is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var v)
            )
            {
                return v;
            }
        }
        return Application.Current.Resources.TryGetValue(key, out var top) ? top : null;
    }

    /// <summary>
    /// Iterate every theme dictionary (every variant in every merged
    /// dictionary) so the caller can mutate a per-theme entry. Used by the
    /// accent-color picker to swap <c>AveliaAccentDefaultBrush</c> across both
    /// Light and Default at runtime.
    /// </summary>
    public static System.Collections.Generic.IEnumerable<ResourceDictionary> EnumerateThemeDictionaries()
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var entry in merged.ThemeDictionaries)
            {
                if (entry.Value is ResourceDictionary dict)
                {
                    yield return dict;
                }
            }
        }
    }
}
