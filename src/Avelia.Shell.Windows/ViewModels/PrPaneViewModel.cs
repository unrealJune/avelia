using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// Right-pane composite VM: PR header (number / branch / base / merge button /
/// checks summary) plus the workspace's file-change list. Loaded by
/// <see cref="WorkspaceViewModel"/> whenever the active workspace changes.
///
/// When the workspace has no associated PR, <see cref="HasPullRequest"/> is
/// <c>false</c> and the PR header collapses; the file list still populates
/// from <see cref="IDiffService.GetWorkspaceDiffAsync"/> (which returns an
/// empty list when nothing's staged).
///
/// <see cref="ActiveTab"/> drives a "Changes" / "Files" SelectorBar in the
/// page header. Both pivots render the same file collection today — the
/// real-backend split between workspace diff and PR diff lands with Chunk 10.
/// The tab still moves so the affordance reads as live UI, not a static
/// label.
/// </summary>
public partial class PrPaneViewModel : ObservableObject
{
    private readonly AveliaServices _services;
    private PullRequestId? _prId;

    /// <summary>
    /// Lifecycle token for the current <see cref="LoadAsync"/> invocation. A
    /// second call cancels and replaces the first so two in-flight loads can't
    /// interleave their state mutations — same pattern as
    /// <see cref="PrReviewViewModel"/>. Without this, fast workspace
    /// renavigation could land the older load's <c>Files.Add</c> after the
    /// newer load's <c>Files.Clear</c> and leave the pane mixing two workspaces.
    /// </summary>
    private CancellationTokenSource? _loadCts;

    public PrPaneViewModel(AveliaServices services)
    {
        _services = services;
    }

    // -------- PR header state --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private bool _hasPullRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    private int _number;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _branch = string.Empty;

    [ObservableProperty]
    private string _base = string.Empty;

    [ObservableProperty]
    private PrStatus _status = PrStatus.Draft;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private bool _mergeReady;

    // -------- Stats row --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalAddDisplay))]
    private int _totalAdd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDelDisplay))]
    private int _totalDel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileCountDisplay))]
    private int _fileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksSummary))]
    private int _checksTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksSummary))]
    private int _checksPassed;

    /// <summary>Display string for the +N chip: "+312". Empty when there are no changes.</summary>
    public string TotalAddDisplay =>
        TotalAdd == 0 ? string.Empty : "+" + TotalAdd.ToString(CultureInfo.InvariantCulture);

    /// <summary>Display string for the -N chip: "-332". Empty when there are no deletions.</summary>
    public string TotalDelDisplay =>
        TotalDel == 0 ? string.Empty : "-" + TotalDel.ToString(CultureInfo.InvariantCulture);

    /// <summary>"6 / 6 checks" — formatted for the stats row.</summary>
    public string ChecksSummary => $"{ChecksPassed}/{ChecksTotal} checks";

    /// <summary>"#1432" link text. Empty when no PR.</summary>
    public string NumberDisplay =>
        HasPullRequest ? "#" + Number.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"10 files" stats label.</summary>
    public string FileCountDisplay => FileCount == 1 ? "1 file" : $"{FileCount} files";

    // -------- Error surface --------

    /// <summary>
    /// Non-null after a failed operation (load / merge). Bound to a thin
    /// InfoBar in the PR header. Cleared on the next successful load and on
    /// any successful merge.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // -------- File list --------

    public ObservableCollection<DiffFileViewModel> Files { get; } = new();

    /// <summary>
    /// Raised when a file row is opened (Enter / click). Chunk 6 will hook
    /// this to navigate the Frame to the PR review page with the file
    /// pre-selected; today the shell only updates the row's focus highlight.
    /// </summary>
    public event EventHandler<RelativePath>? FileOpened;

    // -------- Pivot --------

    /// <summary>
    /// "Changes" or "Files". Driven by the SelectorBar in
    /// <c>WorkspacePage.xaml</c>'s right column. Both values render the same
    /// data today (see class doc); kept as observable state so the strip
    /// reads as live UI and the eventual split is a pure additive change.
    /// </summary>
    [ObservableProperty]
    private string _activeTab = "Changes";

    // -------- Load --------

    /// <summary>
    /// Pull the PR + diff snapshot for <paramref name="workspaceId"/>. Safe to
    /// call repeatedly; each call resets the VM to the new workspace's state.
    /// </summary>
    public async Task LoadAsync(WorkspaceId workspaceId, CancellationToken ct = default)
    {
        // Cancel any in-flight load and replace its CTS, then link the caller's
        // token. Prevents interleaved state mutations if WorkspacePage's
        // LoadAsync is invoked back-to-back (e.g. fast tab switches).
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;

        Files.Clear();
        _prId = null;
        HasPullRequest = false;
        ErrorMessage = null;

        try
        {
            var prResult = await _services
                .PullRequests.GetForWorkspaceAsync(workspaceId, token)
                .ConfigureAwait(true);
            if (prResult.IsSuccess)
            {
                var pr = prResult.Value;
                _prId = pr.Id;
                HasPullRequest = true;
                Number = pr.Number;
                Title = pr.Title;
                Branch = pr.Branch.Value;
                Base = pr.Base.Value;
                Status = pr.Status;
                MergeReady = pr.MergeReady;
                ChecksTotal = pr.Checks.Length;
                var passed = 0;
                for (var i = 0; i < pr.Checks.Length; i++)
                {
                    if (pr.Checks[i].Status.IsPassed)
                    {
                        passed++;
                    }
                }
                ChecksPassed = passed;
            }
            else
            {
                // NotFound for "no PR yet" is expected — don't surface as an
                // error. Other failure shapes (network / auth) become error UI.
                if (!prResult.Error.IsNotFound)
                {
                    ErrorMessage = FormatError(prResult.Error);
                }
                Number = 0;
                Title = string.Empty;
                Branch = string.Empty;
                Base = string.Empty;
                Status = PrStatus.Draft;
                MergeReady = false;
                ChecksTotal = 0;
                ChecksPassed = 0;
            }

            var files = await _services
                .Diffs.GetWorkspaceDiffAsync(workspaceId, token)
                .ConfigureAwait(true);
            var totalAdd = 0;
            var totalDel = 0;
            foreach (var file in files)
            {
                Files.Add(new DiffFileViewModel(file, OnFileOpened));
                totalAdd += file.Add;
                totalDel += file.Del;
            }
            TotalAdd = totalAdd;
            TotalDel = totalDel;
            FileCount = files.Count;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later LoadAsync or the caller cancelled. The
            // newer call has already reset observable state for its workspace.
        }
    }

    private void OnFileOpened(RelativePath path)
    {
        // Single-select focus echo so the row visually highlights even before
        // Chunk 6's PR review page exists.
        foreach (var row in Files)
        {
            row.IsFocused = row.Path.Equals(path);
        }
        FileOpened?.Invoke(this, path);
    }

    // -------- Commands --------

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task Merge(CancellationToken ct)
    {
        if (_prId is null)
        {
            return;
        }
        var result = await _services.PullRequests.MergeAsync(_prId, ct).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            ErrorMessage = null;
            MergeReady = false;
            Status = PrStatus.Merged;
        }
        else
        {
            // Surface the failure as UI rather than letting the merge button
            // go silently inert. Real backends will hit network / conflict
            // failures here regularly.
            ErrorMessage = FormatError(result.Error);
        }
    }

    private bool CanMerge() => HasPullRequest && MergeReady;

    private static string FormatError(AveliaError error) =>
        error.Match<string>(
            onNotFound: resource => $"Not found: {resource}",
            onValidation: msg => msg,
            onUnauthorized: () => "You're not signed in.",
            onConflict: msg => msg,
            onNetwork: msg => $"Network error: {msg}",
            onInternal: msg => $"Internal error: {msg}",
            onExternal: (source, detail) => $"{source}: {detail}"
        );
}
