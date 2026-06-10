using System;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// A file staged in the composer to send with the next message (today: an image
/// pasted from the clipboard, materialised to a temp PNG). Deliberately
/// UI-free — this type is referenced by <see cref="WorkspaceViewModel"/>, which
/// is link-compiled into the pure-net10.0 test assembly — so the thumbnail is
/// rendered from <see cref="Path"/> via a XAML converter rather than carrying a
/// <c>Microsoft.UI.Xaml.Media.ImageSource</c> here.
/// </summary>
public sealed class ComposerAttachmentViewModel
{
    public ComposerAttachmentViewModel(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        FileName = System.IO.Path.GetFileName(path);
    }

    /// <summary>Absolute path to the staged file, passed to the agent as an attachment ref.</summary>
    public string Path { get; }

    /// <summary>Display name (the file's leaf name) shown beside the thumbnail.</summary>
    public string FileName { get; }
}
