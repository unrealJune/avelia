using Avelia.Shell.Windows.Services;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

public class ThemeServiceTests
{
    [Fact]
    public void Theme_DefaultsToSystem()
    {
        var svc = new ThemeService();

        Assert.Equal(AppTheme.System, svc.Theme);
    }

    [Fact]
    public void AccentHex_DefaultsToDesignToken()
    {
        var svc = new ThemeService();

        Assert.Equal(ThemeService.DefaultAccentHex, svc.AccentHex);
    }

    [Fact]
    public void SetTheme_RaisesThemeChanged_WithNewValue()
    {
        var svc = new ThemeService();
        AppTheme? observed = null;
        svc.ThemeChanged += (_, t) => observed = t;

        svc.Theme = AppTheme.Dark;

        Assert.Equal(AppTheme.Dark, observed);
    }

    [Fact]
    public void SetTheme_SameValue_DoesNotRaise()
    {
        var svc = new ThemeService();
        var count = 0;
        svc.ThemeChanged += (_, _) => count++;

        svc.Theme = AppTheme.System; // already System

        Assert.Equal(0, count);
    }

    [Fact]
    public void EffectiveTheme_WhenSystem_ResolvesViaProvider()
    {
        var svc = new ThemeService(systemThemeProvider: () => AppTheme.Light);

        Assert.Equal(AppTheme.Light, svc.EffectiveTheme);
    }

    [Fact]
    public void EffectiveTheme_WhenExplicit_ReturnsExplicitChoice()
    {
        var svc = new ThemeService(systemThemeProvider: () => AppTheme.Light)
        {
            Theme = AppTheme.Dark,
        };

        Assert.Equal(AppTheme.Dark, svc.EffectiveTheme);
    }

    [Fact]
    public void SetAccentHex_RaisesAccentChanged()
    {
        var svc = new ThemeService();
        string? observed = null;
        svc.AccentChanged += (_, hex) => observed = hex;

        svc.AccentHex = "#6CCB5F";

        Assert.Equal("#6CCB5F", observed);
    }

    [Fact]
    public void SetAccentHex_SameValueDifferentCase_DoesNotRaise()
    {
        var svc = new ThemeService();
        var count = 0;
        svc.AccentChanged += (_, _) => count++;

        svc.AccentHex = ThemeService.DefaultAccentHex.ToLowerInvariant();

        Assert.Equal(0, count);
    }

    [Fact]
    public void SetAccentHex_Empty_Throws()
    {
        var svc = new ThemeService();

        Assert.Throws<System.ArgumentException>(() => svc.AccentHex = "");
    }
}
