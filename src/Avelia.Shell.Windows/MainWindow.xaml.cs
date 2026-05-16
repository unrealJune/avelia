using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;

namespace Avelia.Shell.Windows;

/// <summary>
/// Main window for the Avelia shell. Hosts the root <see cref="MainViewModel"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        InitializeComponent();
    }
}
