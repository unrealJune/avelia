namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Top-level section in the nav rail. Drives which page the Frame shows.
/// Inbox / Pinned / History / Archive currently route to placeholder pages
/// (chunks 7+ replace them).
/// </summary>
public enum NavRailSection
{
    Home,
    Inbox,
    Pinned,
    History,
    Archive,
    Settings,
}
