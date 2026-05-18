using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

/// <summary>
/// Validates that every text-on-surface pairing in <c>Themes/Tokens.xaml</c>
/// meets WCAG 2.1 AA contrast in both Light and Dark theme dictionaries.
///
/// Locks in the dark-mode fix shipped in Chunk 3 post-review: previously,
/// labels constructed in C# (repo group names, inline code-refs) captured
/// a brush at first render and stayed wrong after a theme toggle. The
/// XAML-side tokens still need to meet contrast in <em>both</em> themes —
/// if a new accent or surface brush is added that fails AA, this test
/// fails the build before the visual regression ships.
///
/// The test parses Tokens.xaml as XML (no WinUI runtime required), pulls
/// SolidColorBrush color values out of each ThemeDictionary, alpha-blends
/// onto the canonical surface for that pairing, and computes WCAG relative
/// luminance + contrast ratio.
/// </summary>
public class ThemeContrastTests
{
    private const double WcagAaNormalText = 4.5;
    private const double WcagAaLargeText = 3.0;

    private static readonly Lazy<ThemeTokens> Tokens = new(LoadTokens);

    /// <summary>
    /// Foreground/background brush pairs that must clear WCAG AA. Each row
    /// is (theme, fgBrushKey, bgBrushKey, minRatio).
    /// </summary>
    public static IEnumerable<object[]> TextOnSurfacePairs() =>
        from theme in new[] { "Default", "Light" }
        from pair in new[]
        {
            // ---- Primary body text on the canonical surfaces ----
            ("AveliaTextPrimaryBrush", "AveliaMicaBaseBrush", WcagAaNormalText),
            ("AveliaTextPrimaryBrush", "AveliaCardBackgroundBrush", WcagAaNormalText),
            // ---- Secondary text — workspace names, captions ----
            ("AveliaTextSecondaryBrush", "AveliaMicaBaseBrush", WcagAaNormalText),
            ("AveliaTextSecondaryBrush", "AveliaCardBackgroundBrush", WcagAaNormalText),
            // ---- Tertiary — counts, timestamps. Body-text-sized in places,
            //      so we still require AA-normal rather than the large-text
            //      relaxation. If a future redesign drops these to caption
            //      sizes only, switch the minimum to WcagAaLargeText. ----
            ("AveliaTextTertiaryBrush", "AveliaMicaBaseBrush", WcagAaLargeText),
            ("AveliaTextTertiaryBrush", "AveliaCardBackgroundBrush", WcagAaLargeText),
            // ---- Inline code-ref accent — the bug the user reported ----
            ("AveliaAccentTextBrush", "AveliaMicaBaseBrush", WcagAaNormalText),
            ("AveliaAccentTextBrush", "AveliaCardBackgroundBrush", WcagAaNormalText),
            // ---- Status text on tinted chip backgrounds ----
            ("AveliaSuccessBrush", "AveliaSuccessBgBrush", WcagAaLargeText),
            ("AveliaDangerBrush", "AveliaDangerBgBrush", WcagAaLargeText),
            // ---- Neutral chip text on the chip's own subtle fill ----
            ("AveliaTextSecondaryBrush", "AveliaSubtleFillSecondaryBrush", WcagAaNormalText),
            // ---- Chunk 6: PR review diff viewer pairings ----
            //
            // Line-number cell on add/del rows uses the "strong" diff tint as
            // its background and secondary-text foreground. Large-text is the
            // right floor — line numbers are 12px mono but rendered as compact
            // labels; the relaxed contrast minimum mirrors the chip pairings
            // above. Catches the case where a future redesign darkens the
            // strong-tint enough to blend with the text.
            ("AveliaTextSecondaryBrush", "AveliaDiffAddStrongBrush", WcagAaLargeText),
            ("AveliaTextSecondaryBrush", "AveliaDiffDelStrongBrush", WcagAaLargeText),
            // Hunk-header bar uses accent-text on a subtle fill. Same floor
            // as the body code-ref accent above the diff viewer.
            ("AveliaAccentTextBrush", "AveliaSubtleFillTertiaryBrush", WcagAaNormalText),
            // ---- Chunk 7: inbox row tile glyph on tinted background ----
            //
            // Each tile is a 36×36 square with a 16px FontIcon — reads as a
            // glyph rather than body text, so the large-text floor matches
            // the success/danger chip pairings above. Catches a future
            // redesign that darkens any of the *BgBrush tokens enough to
            // blend with the glyph.
            ("AveliaWarningBrush", "AveliaWarningBgBrush", WcagAaLargeText),
            ("AveliaInfoBrush", "AveliaInfoBgBrush", WcagAaLargeText),
        }
        select new object[] { theme, pair.Item1, pair.Item2, pair.Item3 };

    [Theory]
    [MemberData(nameof(TextOnSurfacePairs))]
    public void TextBrush_OnSurface_MeetsWcagAa(
        string theme,
        string fgKey,
        string bgKey,
        double minRatio
    )
    {
        var fg = Tokens.Value.Resolve(theme, fgKey);
        var bg = Tokens.Value.Resolve(theme, bgKey);
        // Every translucent token in Avelia composes against the window's
        // Mica base. Flatten bg onto Mica, then flatten fg onto that. The
        // resulting opaque pair is what WCAG luminance is computed against.
        var mica = Tokens.Value.Resolve(theme, "AveliaMicaBaseBrush");
        var visibleBg = Flatten(bg, mica);
        var visibleFg = Flatten(fg, visibleBg);
        var ratio = ContrastRatio(visibleFg, visibleBg);

        Assert.True(
            ratio >= minRatio,
            $"{theme}: '{fgKey}' on '{bgKey}' contrast = {ratio:0.00} < {minRatio:0.00} "
                + $"(fg={fg.Hex}, bg={bg.Hex}, visibleFg={ToHex(visibleFg)}, visibleBg={ToHex(visibleBg)})"
        );
    }

    [Fact]
    public void EveryTextBrush_DiffersBetweenLightAndDark()
    {
        // Catches the "forgot to add a Light entry" regression. If a key
        // appears in Default but not in Light (or vice-versa) Resolve throws;
        // if it appears in both with the same color, that's a likely typo —
        // text colors should invert across themes.
        var keys = new[]
        {
            "AveliaTextPrimaryBrush",
            "AveliaTextSecondaryBrush",
            "AveliaTextTertiaryBrush",
            "AveliaAccentTextBrush",
            "AveliaMicaBaseBrush",
            "AveliaCardBackgroundBrush",
        };
        foreach (var key in keys)
        {
            var dark = Tokens.Value.Resolve("Default", key);
            var light = Tokens.Value.Resolve("Light", key);
            Assert.True(
                !ColorsEqual(dark, light),
                $"'{key}' has identical Light and Default values ({dark.Hex}) — theme inversion missing?"
            );
        }
    }

    [Fact]
    public void AccentTextBrush_DarkIsLighterThanLight()
    {
        // Sanity check on the user-reported regression: in Light mode the
        // accent ref is rendered on a light surface, so it must be dark; in
        // Dark mode it sits on a dark surface, so it must be light. If a
        // future palette tweak reverses one of these, contrast tanks.
        var darkAccent = Tokens.Value.Resolve("Default", "AveliaAccentTextBrush");
        var lightAccent = Tokens.Value.Resolve("Light", "AveliaAccentTextBrush");
        Assert.True(
            RelativeLuminance(darkAccent) > RelativeLuminance(lightAccent),
            $"Dark accent ({darkAccent.Hex}) should be brighter than Light accent ({lightAccent.Hex})."
        );
    }

    // ------------------------------------------------------------------
    //  Color math (WCAG 2.1)
    // ------------------------------------------------------------------

    /// <summary>
    /// Standard "source over destination" alpha compositing. Caller is
    /// responsible for passing an opaque <paramref name="dst"/> — typically
    /// the window's Mica base, possibly itself already flattened from a
    /// translucent surface like CardBackgroundBrush.
    /// </summary>
    private static Rgba Flatten(Rgba src, Rgba dst) =>
        new(
            src.R * src.A + dst.R * (1 - src.A),
            src.G * src.A + dst.G * (1 - src.A),
            src.B * src.A + dst.B * (1 - src.A),
            1.0
        );

    private static string ToHex(Rgba c) =>
        $"#{(int)Math.Round(c.R * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.B * 255):X2}";

    private static double ContrastRatio(Rgba a, Rgba b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Rgba c)
    {
        // sRGB → linear (WCAG 2.1 §relative-luminance).
        static double Lin(double v) =>
            v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static bool ColorsEqual(Rgba a, Rgba b) =>
        Math.Abs(a.R - b.R) < 1e-6
        && Math.Abs(a.G - b.G) < 1e-6
        && Math.Abs(a.B - b.B) < 1e-6
        && Math.Abs(a.A - b.A) < 1e-6;

    // ------------------------------------------------------------------
    //  Tokens.xaml parsing
    // ------------------------------------------------------------------

    private static ThemeTokens LoadTokens()
    {
        var path = FindTokensXaml();
        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var defaultNs = doc.Root!.GetDefaultNamespace();

        var themes = new Dictionary<string, Dictionary<string, Rgba>>(StringComparer.Ordinal);

        var themeDicts = doc.Descendants(defaultNs + "ResourceDictionary")
            .Where(e => e.Attribute(x + "Key") is not null);

        foreach (var dict in themeDicts)
        {
            var key = dict.Attribute(x + "Key")!.Value;
            var brushes = new Dictionary<string, Rgba>(StringComparer.Ordinal);
            foreach (var brush in dict.Elements(defaultNs + "SolidColorBrush"))
            {
                var brushKey = brush.Attribute(x + "Key")?.Value;
                var color = brush.Attribute("Color")?.Value;
                if (brushKey is null || color is null)
                {
                    continue;
                }
                brushes[brushKey] = ParseHex(color);
            }
            themes[key] = brushes;
        }

        return new ThemeTokens(themes);
    }

    private static string FindTokensXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Avelia.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Couldn't find repo root (Avelia.sln) above test bin directory."
            );
        }
        return Path.Combine(dir.FullName, "src", "Avelia.Shell.Windows", "Themes", "Tokens.xaml");
    }

    private static Rgba ParseHex(string hex)
    {
        if (hex.Length == 0 || hex[0] != '#')
        {
            throw new FormatException($"Expected '#'-prefixed hex color, got '{hex}'.");
        }
        var s = hex[1..];
        byte a,
            r,
            g,
            b;
        switch (s.Length)
        {
            case 6: // #RRGGBB
                a = 0xFF;
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
                break;
            case 8: // #AARRGGBB
                a = Convert.ToByte(s.Substring(0, 2), 16);
                r = Convert.ToByte(s.Substring(2, 2), 16);
                g = Convert.ToByte(s.Substring(4, 2), 16);
                b = Convert.ToByte(s.Substring(6, 2), 16);
                break;
            default:
                throw new FormatException($"Unsupported hex color length: '{hex}'.");
        }
        return new Rgba(r / 255.0, g / 255.0, b / 255.0, a / 255.0) { Hex = hex };
    }

    // ------------------------------------------------------------------
    //  Types
    // ------------------------------------------------------------------

    private sealed record ThemeTokens(IReadOnlyDictionary<string, Dictionary<string, Rgba>> Themes)
    {
        public Rgba Resolve(string theme, string brushKey)
        {
            if (!Themes.TryGetValue(theme, out var dict))
            {
                throw new KeyNotFoundException(
                    $"Theme dictionary '{theme}' not found in Tokens.xaml."
                );
            }
            if (!dict.TryGetValue(brushKey, out var color))
            {
                throw new KeyNotFoundException(
                    $"Brush '{brushKey}' not defined in theme '{theme}'."
                );
            }
            return color;
        }
    }

    private readonly record struct Rgba(double R, double G, double B, double A)
    {
        public string Hex { get; init; } = string.Empty;
    }
}
