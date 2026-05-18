using System.Collections.Generic;
using System.Globalization;
using Avelia.Core.Abstractions;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Projection of one <see cref="DiffLine"/> for the unified-diff viewer. Pre-computes
/// the per-row display strings + the booleans the row template uses to pick its
/// background brush, so the XAML stays free of converters and the F# DU stays on
/// the F# side of the boundary.
///
/// Pure DTO — no <see cref="System.ComponentModel.INotifyPropertyChanged"/>. Diff
/// data is immutable once loaded; lists rebuild on file selection rather than
/// mutating row state.
/// </summary>
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffLine line)
    {
        LineNumber = line.LineNumber;
        LineNumberDisplay = line.LineNumber.ToString(CultureInfo.InvariantCulture);
        Kind = line.Kind;
        Text = line.Text;
        IsAddition = line.Kind.IsAddition;
        IsDeletion = line.Kind.IsDeletion;
        IsContext = line.Kind.IsContext;
        Sign =
            IsAddition ? "+"
            : IsDeletion ? "-"
            : " ";
    }

    public int LineNumber { get; }

    public string LineNumberDisplay { get; }

    public DiffLineKind Kind { get; }

    public string Text { get; }

    public string Sign { get; }

    public bool IsAddition { get; }

    public bool IsDeletion { get; }

    public bool IsContext { get; }
}

/// <summary>
/// Projection of one <see cref="DiffHunk"/>. Carries the hunk header (rendered as
/// the accent-text bar above the lines) plus the projected lines.
/// </summary>
public sealed class DiffHunkViewModel
{
    public DiffHunkViewModel(DiffHunk hunk)
    {
        File = hunk.File;
        Header = hunk.Header;
        var rows = new List<DiffLineViewModel>(hunk.Lines.Length);
        foreach (var line in hunk.Lines)
        {
            rows.Add(new DiffLineViewModel(line));
        }
        Lines = rows;
    }

    public RelativePath File { get; }

    public string Header { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }
}
