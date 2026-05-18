using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Pages.SettingsSubpages;

public sealed partial class ProfileSubpage : UserControl
{
    private ProfileSubpageViewModel? _viewModel;

    public ProfileSubpage()
    {
        InitializeComponent();
    }

    public ProfileSubpageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }
}
