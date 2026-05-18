using System;
using System.Globalization;
using System.Windows.Input;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One row in the right-pane file list. Pre-splits the relative path into
/// folder + filename so the row template can render the muted-folder /
/// strong-filename pattern without converters.
///
/// Owns no state of its own beyond the typed snapshot from <see cref="DiffFile"/>;
/// <see cref="IsFocused"/> mirrors the design's "active file" highlight and is
/// re-set by the parent <see cref="PrPaneViewModel"/> on selection.
/// </summary>
public partial class DiffFileViewModel : ObservableObject
{
    public DiffFileViewModel(DiffFile file, Action<RelativePath> onOpen)
    {
        Path = file.Path;
        Folder = file.Path.Folder;
        FileName = file.Path.FileName;
        Add = file.Add;
        Del = file.Del;
        Kind = file.Kind;
        KindBadge = KindToBadge(file.Kind);
        _isFocused = file.IsFocused;
        AddDisplay = "+" + file.Add.ToString(CultureInfo.InvariantCulture);
        DelDisplay = "-" + file.Del.ToString(CultureInfo.InvariantCulture);

        OpenCommand = new RelayCommand(() => onOpen(Path));
    }

    public RelativePath Path { get; }

    public string Folder { get; }

    public string FileName { get; }

    public int Add { get; }

    public int Del { get; }

    public string AddDisplay { get; }

    public string DelDisplay { get; }

    public DiffKind Kind { get; }

    /// <summary>Single-character badge: M / A / D / R.</summary>
    public string KindBadge { get; }

    [ObservableProperty]
    private bool _isFocused;

    public ICommand OpenCommand { get; }

    private static string KindToBadge(DiffKind kind) =>
        // F# Match enforces exhaustiveness — adding a new DiffKind case
        // breaks compilation here until the badge mapping is updated.
        kind.Match<string>(
            onModified: () => "M",
            onAdded: () => "A",
            onDeleted: () => "D",
            onRenamed: _ => "R"
        );
}
