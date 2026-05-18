using System;
using System.Collections.Generic;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Left-column file tree for the PR review page. Renders a flat, single-select
/// list of <see cref="DiffFileViewModel"/>s (the design's tree is one level deep
/// today; a real nested tree lands when the diff includes folder rollups —
/// Chunk 10).
///
/// Clicking a row raises <see cref="FileSelected"/>; the page swaps the diff
/// viewer's hunks in response. Highlighting is owned by the row VM via
/// <see cref="DiffFileViewModel.IsFocused"/>, which <see cref="PrReviewViewModel"/>
/// re-points on every selection.
/// </summary>
public sealed partial class PrFileTree : UserControl
{
    public PrFileTree()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty FilesProperty = DependencyProperty.Register(
        nameof(Files),
        typeof(IEnumerable<DiffFileViewModel>),
        typeof(PrFileTree),
        new PropertyMetadata(null)
    );

    public IEnumerable<DiffFileViewModel>? Files
    {
        get => (IEnumerable<DiffFileViewModel>?)GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    /// <summary>Raised when a row is clicked (mouse or keyboard).</summary>
    public event EventHandler<DiffFileViewModel>? FileSelected;

    private void OnFileItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiffFileViewModel file)
        {
            FileSelected?.Invoke(this, file);
        }
    }
}
