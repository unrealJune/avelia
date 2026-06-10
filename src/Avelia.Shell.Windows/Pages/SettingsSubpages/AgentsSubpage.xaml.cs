using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Pages.SettingsSubpages;

/// <summary>
/// Agents &amp; Models subpage — set the default model, reasoning effort, and
/// context tier via the unified <see cref="ViewModels.ModelBarViewModel"/>, plus
/// the GitHub token. No visual-tree walks live here.
/// </summary>
public sealed partial class AgentsSubpage : UserControl
{
    private AgentsSubpageViewModel? _viewModel;

    public AgentsSubpage()
    {
        InitializeComponent();
    }

    public AgentsSubpageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    // PasswordBox.Password isn't bindable, so mirror it into the VM on change.
    private void GitHubTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is PasswordBox box)
        {
            _viewModel.GitHubToken = box.Password;
        }
    }
}
