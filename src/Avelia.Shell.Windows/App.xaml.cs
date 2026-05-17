using Avelia.Core;
using Avelia.Shell.Windows.Services;
using Microsoft.UI.Xaml;
using global::Windows.UI.ViewManagement;

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
        Services = Composition.buildStubServices();
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
