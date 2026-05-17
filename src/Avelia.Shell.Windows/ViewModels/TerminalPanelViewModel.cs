using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Sticky bottom panel on the workspace right pane — terminal/run output. For
/// Chunk 4 this is a typed snapshot of the active branch's prompt line; real
/// live output streams in once <see cref="IRunService.ObserveOutput"/> lands
/// (deferred per the plan).
///
/// The tab strip toggles <see cref="ActiveTab"/> between <c>"Terminal"</c>
/// (default) and <c>"Run"</c>. The Run body is an empty placeholder today;
/// <see cref="RunCommand"/> flips to the Run tab — real run execution lands
/// with Chunk 10.
/// </summary>
public partial class TerminalPanelViewModel : ObservableObject
{
    /// <summary>"Run" or "Terminal" — see class doc.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunActive))]
    [NotifyPropertyChangedFor(nameof(IsTerminalActive))]
    private string _activeTab = "Terminal";

    public bool IsRunActive => ActiveTab == "Run";

    public bool IsTerminalActive => ActiveTab == "Terminal";

    /// <summary>
    /// Pre-formatted prompt line, e.g. <c>"→ kampala-v3 git:(archive-in-repo-details)"</c>.
    /// Mirrors what the design renders verbatim; the cursor blink ships as
    /// an animation in the XAML control.
    /// </summary>
    [ObservableProperty]
    private string _promptLine = string.Empty;

    [ObservableProperty]
    private string _branch = string.Empty;

    [ObservableProperty]
    private string _base = string.Empty;

    public void Load(Workspace workspace)
    {
        Branch = workspace.Branch.Value;
        Base = workspace.Base.Value;
        PromptLine = $"→ {Base} git:({Branch})";
    }

    /// <summary>
    /// Clear branch / base / prompt — used when the parent workspace fetch
    /// fails so a stale prompt from the previous workspace doesn't linger.
    /// </summary>
    public void Reset()
    {
        Branch = string.Empty;
        Base = string.Empty;
        PromptLine = string.Empty;
    }

    /// <summary>
    /// Flip the strip to the "Run" tab. Stub for Chunk 4 — real run
    /// execution (process start, stdout streaming) wires in with Chunk 10's
    /// <c>IRunService.ObserveOutput</c>. Keeping the command live now means
    /// the button isn't dead UI in front of users.
    /// </summary>
    [RelayCommand]
    private void Run()
    {
        ActiveTab = "Run";
    }
}
