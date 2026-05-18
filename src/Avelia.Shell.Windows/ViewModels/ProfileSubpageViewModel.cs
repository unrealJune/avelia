using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// VM for the Profile subpage. Holds the user's display name, email, and avatar
/// initials. Real values are wired in when account integration lands; for now
/// the design's stub user (June Philip) is the initial state so the layout
/// renders as designed.
/// </summary>
public partial class ProfileSubpageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _displayName = "June Philip";

    [ObservableProperty]
    private string _email = "mail.junephilip@gmail.com";

    /// <summary>Two-letter avatar fallback. Recomputed when DisplayName changes.</summary>
    public string Initials => DeriveInitials(DisplayName);

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    private static string DeriveInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";
        var parts = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "?";
        if (parts.Length == 1)
            return parts[0].Substring(0, System.Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
    }
}
