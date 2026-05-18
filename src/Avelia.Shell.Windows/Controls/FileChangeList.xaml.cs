using System.Collections.Generic;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Scrollable file-change list for the workspace right pane. Each row is a
/// projected <see cref="DiffFileViewModel"/>; clicking a row invokes the row's
/// <c>OpenCommand</c> (which routes through <see cref="PrPaneViewModel.FileOpened"/>
/// — Chunk 6 will hook that to navigate to PR review).
/// </summary>
public sealed partial class FileChangeList : UserControl
{
    public FileChangeList()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty FilesProperty = DependencyProperty.Register(
        nameof(Files),
        typeof(IEnumerable<DiffFileViewModel>),
        typeof(FileChangeList),
        new PropertyMetadata(null)
    );

    public IEnumerable<DiffFileViewModel>? Files
    {
        get => (IEnumerable<DiffFileViewModel>?)GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    private void OnFileItemClick(object sender, ItemClickEventArgs e)
    {
        // OpenCommand is nullable (PR Review constructs rows without one);
        // workspace right-pane rows always have it, but guard defensively.
        if (
            e.ClickedItem is DiffFileViewModel file
            && file.OpenCommand is { } cmd
            && cmd.CanExecute(null)
        )
        {
            cmd.Execute(null);
        }
    }
}
