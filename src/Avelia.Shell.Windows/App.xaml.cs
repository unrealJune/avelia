using System;
using System.IO;
using System.Threading.Tasks;
using Avelia.Core;
using Avelia.Services;
using Avelia.Shell.Windows.Services;
using Avelia.Shell.Windows.Terminal;
using global::Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace Avelia.Shell.Windows;

/// <summary>
/// Application entry point for the Avelia WinUI 3 shell.
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        // Wire crash logging before anything else so a failure during service
        // composition or launch lands in a log file instead of vanishing as an
        // opaque stowed-exception crash with no UI.
        WireCrashLogging();

        InitializeComponent();
        ThemeService = new ThemeService(systemThemeProvider: ReadSystemTheme);
        // Real backend (git + GitHub auth + Copilot) is opt-in via AVELIA_REAL=1
        // so design-time data and the E2E suite keep running on the stub graph.
        Services = UseRealBackend()
            ? RealComposition.buildServices(new ConPtyTerminalSessionFactory())
            : Composition.buildStubServices();
    }

    private void WireCrashLogging()
    {
        UnhandledException += (_, e) =>
            CrashLog.Write("Application.UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
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
        // Register for OS toast notifications (turn-complete alerts). Registration
        // can fail (e.g. a packaged app missing the toast-activator manifest
        // declaration); a failure here must not crash the whole app before any
        // window is shown, so it is logged and swallowed.
        TryRegisterNotifications();

        _mainWindow = new MainWindow(ThemeService, Services);
        _mainWindow.Activate();
    }

    private static void TryRegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.Register();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    AppNotificationManager.Default.Unregister();
                }
                catch (Exception ex)
                {
                    CrashLog.Write("AppNotificationManager.Unregister", ex);
                }
            };
        }
        catch (Exception ex)
        {
            CrashLog.Write("AppNotificationManager.Register", ex);
        }
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

/// <summary>
/// Best-effort crash logger. The shell otherwise has no global exception sink,
/// so an unhandled exception during startup surfaces only as an opaque
/// stowed-exception (combase / Microsoft.UI.Xaml) process crash. Appends to
/// <c>%LOCALAPPDATA%/Avelia/crash.log</c>; logging never throws.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Avelia"
            );
            Directory.CreateDirectory(dir);
            var entry =
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "crash.log"), entry);
            }
        }
        catch
        {
            // A logger that throws would defeat its own purpose.
        }
    }
}
