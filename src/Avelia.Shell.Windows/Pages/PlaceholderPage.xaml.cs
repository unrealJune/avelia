using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Avelia.Shell.Windows.Pages;

/// <summary>
/// Generic stub page. The Frame's navigation parameter is a
/// <see cref="PlaceholderPageArgs"/> that sets the visible title and subtitle.
/// </summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PlaceholderPageArgs args)
        {
            PageTitle.Text = args.Title;
            PageSubtitle.Text = args.Subtitle;
        }
    }
}

public sealed record PlaceholderPageArgs(string Title, string Subtitle);
