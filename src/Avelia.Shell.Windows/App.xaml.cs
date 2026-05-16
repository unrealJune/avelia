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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
