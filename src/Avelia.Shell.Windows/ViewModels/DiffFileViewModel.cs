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
    /// <summary>
    /// Construct a row VM. <paramref name="onOpen"/> is optional: pass
    /// <c>null</c> when the parent surface routes clicks through its own
    /// container (e.g. <c>PrFileTree</c> uses <c>ListView.ItemClick</c> and
    /// has no use for the row's command). When null, <see cref="OpenCommand"/>
    /// is also null and binding to it is a no-op — leaving a no-op delegate
    /// would silently swallow command invocations from future callers.
    /// </summary>
    public DiffFileViewModel(DiffFile file, Action<RelativePath>? onOpen)
    {
        Path = file.Path;
        Folder = file.Path.Folder;
        FileName = file.Path.FileName;
        Add = file.Add;
        Del = file.Del;
        Kind = file.Kind;
        KindBadge = KindToBadge(file.Kind);
        _isFocused = file.IsFocused;
        AddDisplay =
            file.Add == 0 ? string.Empty : "+" + file.Add.ToString(CultureInfo.InvariantCulture);
        DelDisplay =
            file.Del == 0 ? string.Empty : "-" + file.Del.ToString(CultureInfo.InvariantCulture);

        OpenCommand = onOpen is null ? null : new RelayCommand(() => onOpen(Path));
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

    /// <summary>
    /// Null when the row VM was constructed without an <c>onOpen</c> callback
    /// (PR Review file tree path). XAML <c>{x:Bind OpenCommand}</c> safely
    /// no-ops on null commands.
    /// </summary>
    public ICommand? OpenCommand { get; }

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
