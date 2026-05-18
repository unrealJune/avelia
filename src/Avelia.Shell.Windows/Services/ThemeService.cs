using System;

namespace Avelia.Shell.Windows.Services;

/// <summary>
/// The three theme choices the user can pick. <see cref="System"/> defers to the
/// OS-reported color mode.
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Single source of truth for the shell's theme and accent color.
///
/// Intentionally pure .NET — no Microsoft.UI.* references — so the test project
/// (which targets net10.0, not net10.0-windows) can compile and exercise this
/// class via file-link. The MainWindow subscribes to <see cref="ThemeChanged"/>
/// and applies the chosen theme to its root element.
/// </summary>
public sealed class ThemeService
{
    private readonly Func<AppTheme> _systemThemeProvider;
    private AppTheme _theme = AppTheme.System;
    private string _accentHex = DefaultAccentHex;

    /// <summary>The default Windows 11 dark-mode accent (Sky Blue) — also matches the design token.</summary>
    public const string DefaultAccentHex = "#4CC2FF";

    public ThemeService(Func<AppTheme>? systemThemeProvider = null)
    {
        _systemThemeProvider = systemThemeProvider ?? (() => AppTheme.Dark);
    }

    /// <summary>User-selected theme. Setting to the same value is a no-op (no event).</summary>
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value)
            {
                return;
            }
            _theme = value;
            ThemeChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// Resolved theme: if <see cref="Theme"/> is <see cref="AppTheme.System"/> this
    /// invokes the system-theme provider, otherwise returns <see cref="Theme"/> directly.
    /// </summary>
    public AppTheme EffectiveTheme => _theme == AppTheme.System ? _systemThemeProvider() : _theme;

    /// <summary>Accent color as a CSS-style hex string (e.g. "#4CC2FF").</summary>
    public string AccentHex
    {
        get => _accentHex;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Accent hex must be a non-empty color string.",
                    nameof(value)
                );
            }
            if (string.Equals(_accentHex, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _accentHex = value;
            AccentChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<AppTheme>? ThemeChanged;
    public event EventHandler<string>? AccentChanged;
}
