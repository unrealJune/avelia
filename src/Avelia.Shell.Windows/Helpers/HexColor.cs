using System;

namespace Avelia.Shell.Windows.Helpers;

/// <summary>
/// Parses CSS-style hex color strings into WinUI <see cref="global::Windows.UI.Color"/>.
///
/// Format follows the convention documented at the top of <c>Themes/Tokens.xaml</c>:
/// <c>#RRGGBB</c> (alpha implicit FF) or <c>#AARRGGBB</c> (alpha first). The
/// leading <c>#</c> is optional. Anything else returns <c>false</c>.
///
/// Centralized so the accent-color picker and the theme-resource swap don't
/// disagree about byte ordering — an earlier draft had two parsers, one of
/// which treated 8-char as <c>RRGGBBAA</c>, silently producing different colors
/// from the same input string.
/// </summary>
public static class HexColor
{
    public static bool TryParse(string? hex, out global::Windows.UI.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }
        var trimmed = hex.AsSpan().Trim();
        if (trimmed.Length > 0 && trimmed[0] == '#')
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.Length != 6 && trimmed.Length != 8)
        {
            return false;
        }
        try
        {
            byte a = 0xFF;
            var offset = 0;
            if (trimmed.Length == 8)
            {
                a = byte.Parse(
                    trimmed[..2],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                offset = 2;
            }
            var r = byte.Parse(
                trimmed.Slice(offset + 0, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture
            );
            var g = byte.Parse(
                trimmed.Slice(offset + 2, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture
            );
            var b = byte.Parse(
                trimmed.Slice(offset + 4, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture
            );
            color = global::Windows.UI.Color.FromArgb(a, r, g, b);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
