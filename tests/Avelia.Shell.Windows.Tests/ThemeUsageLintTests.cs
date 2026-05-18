using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

/// <summary>
/// Static lint over every XAML file under <c>src/Avelia.Shell.Windows/</c>:
/// catches the "someone wrote <c>Foreground=&quot;#...&quot;</c>" regression
/// that bypasses the theme dictionary. The Chunk-3 dark-mode bug had this
/// shape in C# code (<c>Application.Current.Resources[...]</c> captures);
/// the analogous shape in XAML is a hex literal on a color-bearing attribute.
///
/// Doesn't replace a full runtime accessibility scan — that lives in
/// <c>Avelia.E2E</c> once Axe.Windows.Automation is wired up (Chunk 9).
/// This is the cheap version that catches the bug class at source-code
/// level, in milliseconds, with no app needed.
///
/// All literal color values <em>must</em> live in <c>Tokens.xaml</c> so they
/// have a Light + Default twin and re-resolve on theme switch.
/// </summary>
public class ThemeUsageLintTests
{
    /// <summary>
    /// Attributes whose value, if it starts with '#', is a hardcoded color
    /// and therefore can't track theme. (Static colors like
    /// <c>Transparent</c> / <c>White</c> / brush references in
    /// <c>{ThemeResource ...}</c> / <c>{StaticResource ...}</c> form are
    /// fine and ignored.)
    /// </summary>
    private static readonly string[] ColorBearingAttributes =
    {
        "Foreground",
        "Background",
        "BorderBrush",
        "Fill",
        "Stroke",
        "Color",
    };

    private static readonly Regex HexAttrRegex = new(
        @"(?<attr>\w+)\s*=\s*""\s*(?<value>#[0-9A-Fa-f]{3,8})\s*""",
        RegexOptions.Compiled
    );

    [Fact]
    public void NoHardcodedHexColors_OutsideTokens()
    {
        var shellRoot = FindShellRoot();
        var tokensPath = Path.Combine(shellRoot, "Themes", "Tokens.xaml");
        var violations = new List<string>();

        foreach (var xaml in EnumerateXamlFiles(shellRoot))
        {
            // Tokens.xaml IS the place where literal hex colors live.
            if (
                string.Equals(
                    Path.GetFullPath(xaml),
                    Path.GetFullPath(tokensPath),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var content = File.ReadAllText(xaml);
            // Strip XAML comments so commented-out swatches don't trip the lint.
            var stripped = Regex.Replace(
                content,
                @"<!--.*?-->",
                string.Empty,
                RegexOptions.Singleline
            );

            foreach (Match m in HexAttrRegex.Matches(stripped))
            {
                var attr = m.Groups["attr"].Value;
                if (!ColorBearingAttributes.Contains(attr, StringComparer.Ordinal))
                {
                    continue;
                }
                var value = m.Groups["value"].Value;
                var lineNumber = LineOf(content, m.Index);
                violations.Add(
                    $"{RelativePath(shellRoot, xaml)}:{lineNumber}  {attr}=\"{value}\" — use a brush from Tokens.xaml via {{ThemeResource ...}}."
                );
            }
        }

        Assert.True(
            violations.Count == 0,
            "Found hardcoded color literals in XAML — these bypass the theme dictionary "
                + "and won't track Light/Dark toggles. Move them to Themes/Tokens.xaml and "
                + "reference via {ThemeResource ...}:\n\n  "
                + string.Join("\n  ", violations)
        );
    }

    [Fact]
    public void NoStaticResourceOnColorAttributes_ShouldBeThemeResource()
    {
        // {StaticResource ...} on a Foreground/Background captures the brush
        // at parse time and won't update on theme switch. Theme tokens MUST
        // be referenced as {ThemeResource ...}. (Non-color resources like
        // CornerRadius / FontFamily are fine as StaticResource.)
        var shellRoot = FindShellRoot();
        var attrPattern = string.Join("|", ColorBearingAttributes);
        var pattern = new Regex(
            $@"(?<attr>{attrPattern})\s*=\s*""\s*\{{\s*StaticResource\s+(?<key>[A-Za-z0-9_]+)\s*\}}\s*""",
            RegexOptions.Compiled
        );

        var violations = new List<string>();
        foreach (var xaml in EnumerateXamlFiles(shellRoot))
        {
            var content = File.ReadAllText(xaml);
            var stripped = Regex.Replace(
                content,
                @"<!--.*?-->",
                string.Empty,
                RegexOptions.Singleline
            );
            foreach (Match m in pattern.Matches(stripped))
            {
                var key = m.Groups["key"].Value;
                var attr = m.Groups["attr"].Value;
                // The Avelia*Brush keys live in theme dictionaries; any
                // StaticResource reference to one of them is a bug.
                if (!key.Contains("Brush", StringComparison.Ordinal))
                {
                    continue;
                }
                var lineNumber = LineOf(content, m.Index);
                violations.Add(
                    $"{RelativePath(shellRoot, xaml)}:{lineNumber}  {attr}=\"{{StaticResource {key}}}\" — use {{ThemeResource}} for theme-tracked brushes."
                );
            }
        }

        Assert.True(
            violations.Count == 0,
            "Found {StaticResource} bindings on color attributes — these freeze the brush "
                + "at first paint and bypass theme tracking. Switch to {ThemeResource ...}:\n\n  "
                + string.Join("\n  ", violations)
        );
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static IEnumerable<string> EnumerateXamlFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase
                )
                && !p.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase
                )
                && !p.Contains(
                    $"{Path.DirectorySeparatorChar}AppX{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase
                )
            );

    private static int LineOf(string content, int charIndex) =>
        content.Take(charIndex).Count(c => c == '\n') + 1;

    private static string RelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath);

    private static string FindShellRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Avelia.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Couldn't find repo root above test bin directory."
            );
        }
        return Path.Combine(dir.FullName, "src", "Avelia.Shell.Windows");
    }
}
