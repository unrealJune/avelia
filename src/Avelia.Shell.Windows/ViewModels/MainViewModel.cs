using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// View-model backing <see cref="MainWindow"/>. Exposes a window title and a smoke-test
/// greet command. The view-model lives in the shell project because, per the architecture
/// rules, view-models are presentation concerns.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Avelia";

    [ObservableProperty]
    private string _greeting = "Welcome to Avelia.";

    [RelayCommand]
    private void Greet()
    {
        Greeting = "Hello from Avelia.";
    }
}
