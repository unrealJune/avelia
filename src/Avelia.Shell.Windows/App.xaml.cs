using System;
using Avelia.Core;
using Avelia.Services;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.Terminal;
using global::Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;

namespace Avelia.Shell.Windows;

/// <summary>
/// Application entry point for the Avelia WinUI 3 shell.
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
        ThemeService = new ThemeService(systemThemeProvider: ReadSystemTheme);
        // Real backend (git + GitHub auth + Copilot) is opt-in via AVELIA_REAL=1
        // so design-time data and the E2E suite keep running on the stub graph.
        Services = UseRealBackend()
            ? RealComposition.buildServices(new ConPtyTerminalSessionFactory())
            : Composition.buildStubServices();
    }

    private static bool UseRealBackend()
    {
        var flag = Environment.GetEnvironmentVariable("AVELIA_REAL");
        return string.Equals(flag, "1", StringComparison.Ordinal)
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shared theme state for the app. Owned by App so a single instance survives
    /// page navigation and is observable from any view-model.
    /// </summary>
    public ThemeService ThemeService { get; }

    /// <summary>
    /// Service graph (stub-backed for now; swappable for a real-backend variant
    /// once persistence/VCS/agent adapters land — see <c>docs/plans/winui-conductor-fluent.md</c>
    /// Chunk 10).
    /// </summary>
    public AveliaServices Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow(ThemeService, Services);
        _mainWindow.Activate();
    }

    private static AppTheme ReadSystemTheme()
    {
        // UISettings is the Windows API for reading the system theme without
        // creating a UI element. Returns Light/Dark; we map Background → app theme.
        var settings = new UISettings();
        var background = settings.GetColorValue(UIColorType.Background);
        // Light background ⇒ user's OS is in light mode.
        var isLight = background.R > 128 && background.G > 128 && background.B > 128;
        return isLight ? AppTheme.Light : AppTheme.Dark;
    }
}
