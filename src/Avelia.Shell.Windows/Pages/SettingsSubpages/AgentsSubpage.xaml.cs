using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Pages.SettingsSubpages;

/// <summary>
/// Agents &amp; Models subpage — pick the default model and toggle extended
/// thinking. The model radio group binds two-way to <see cref="AgentModelOption.IsSelected"/>;
/// the toggle is bound two-way to the VM. No visual-tree walks live here.
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
}
