using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Pages.SettingsSubpages;

/// <summary>
/// Holding card for Settings sections that don't have content yet
/// (Repositories, Keyboard, Notifications, Privacy, Updates, About).
/// </summary>
public sealed partial class PlaceholderSubpage : UserControl
{
    public PlaceholderSubpage()
    {
        InitializeComponent();
    }

    public PlaceholderSubpage(string title)
        : this()
    {
        TitleText.Text = title;
    }
}
