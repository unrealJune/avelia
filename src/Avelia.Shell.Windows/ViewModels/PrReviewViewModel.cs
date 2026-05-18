using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// View-model for <c>PrReviewPage</c>. Loads the workspace's PR, projects the
/// file list into <see cref="Files"/>, and lazily loads <see cref="DiffHunk"/>s
/// for the selected file via <see cref="IDiffService.GetHunksAsync"/>.
///
/// Selection is driven by <see cref="SelectFileAsync"/> or by setting
/// <see cref="SelectedFile"/> directly — the file row click in the tree binds
/// to <see cref="SelectFileCommand"/> so the keyboard/mouse paths share one
/// entry point.
///
/// The seven <c>Checks*</c> properties drive the InfoBar above the diff:
/// severity flips to Warning when any check is non-passing, the message
/// summarizes the pass/fail count.
/// </summary>
public partial class PrReviewViewModel : ObservableObject
{
    private readonly AveliaServices _services;
    private PullRequestId? _prId;
    private WorkspaceId? _workspaceId;

    /// <summary>
    /// Lifecycle token for the current <see cref="LoadAsync"/> invocation. A
    /// second call cancels and replaces the first so two in-flight loads can't
    /// interleave their state mutations — without this, fast renavigation
    /// (or a future "refresh" command) would clear collections partway through
    /// the previous fill and leave the UI in a torn state.
    /// </summary>
    private CancellationTokenSource? _loadCts;

    public PrReviewViewModel(AveliaServices services)
    {
        _services = services;
        SelectFileCommand = new AsyncRelayCommand<DiffFileViewModel?>(OnSelectFileCommand);
    }

    // -------- PR header --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowChecksInfoBar))]
    [NotifyPropertyChangedFor(nameof(AllChecksPassed))]
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
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private PrStatus _status = PrStatus.Draft;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private bool _mergeReady;

    /// <summary>"#1432" link text; empty when no PR.</summary>
    public string NumberDisplay =>
        HasPullRequest ? "#" + Number.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Human-readable PR status for the pill: "Draft", "Open", "In review", "Approved", "Merged", "Closed".</summary>
    public string StatusLabel => FormatStatus(Status);

    // -------- Stats --------

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalAddDisplay))]
    private int _totalAdd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDelDisplay))]
    private int _totalDel;

    public string FileCountDisplay => FileCount == 1 ? "1 file" : $"{FileCount} files";

    public string TotalAddDisplay =>
        TotalAdd == 0 ? string.Empty : "+" + TotalAdd.ToString(CultureInfo.InvariantCulture);

    public string TotalDelDisplay =>
        TotalDel == 0 ? string.Empty : "-" + TotalDel.ToString(CultureInfo.InvariantCulture);

    // -------- Checks --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksMessage))]
    [NotifyPropertyChangedFor(nameof(ChecksSeverityName))]
    [NotifyPropertyChangedFor(nameof(AllChecksPassed))]
    [NotifyPropertyChangedFor(nameof(ShowChecksInfoBar))]
    private int _checksTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksMessage))]
    [NotifyPropertyChangedFor(nameof(ChecksSeverityName))]
    [NotifyPropertyChangedFor(nameof(AllChecksPassed))]
    [NotifyPropertyChangedFor(nameof(ShowChecksInfoBar))]
    private int _checksPassed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksMessage))]
    [NotifyPropertyChangedFor(nameof(ChecksSeverityName))]
    [NotifyPropertyChangedFor(nameof(AllChecksPassed))]
    [NotifyPropertyChangedFor(nameof(ShowChecksInfoBar))]
    private int _checksFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksMessage))]
    [NotifyPropertyChangedFor(nameof(ChecksSeverityName))]
    [NotifyPropertyChangedFor(nameof(AllChecksPassed))]
    [NotifyPropertyChangedFor(nameof(ShowChecksInfoBar))]
    private int _checksWarning;

    public bool AllChecksPassed =>
        HasPullRequest
        && ChecksTotal > 0
        && ChecksFailed == 0
        && ChecksWarning == 0
        && ChecksPassed == ChecksTotal;

    /// <summary>
    /// True only once the PR data is loaded AND there are checks to summarize.
    /// Bound to <c>InfoBar.IsOpen</c> so the bar doesn't flash green ("Success" /
    /// "All 0 checks passed") between page paint and <see cref="LoadAsync"/>
    /// populating the counters.
    /// </summary>
    public bool ShowChecksInfoBar => HasPullRequest && ChecksTotal > 0;

    /// <summary>One-line message for the InfoBar above the diff.</summary>
    public string ChecksMessage
    {
        get
        {
            if (!HasPullRequest || ChecksTotal == 0)
            {
                return string.Empty;
            }
            if (ChecksFailed > 0)
            {
                return $"{ChecksFailed} check{(ChecksFailed == 1 ? "" : "s")} failed";
            }
            if (ChecksWarning > 0)
            {
                return $"{ChecksPassed}/{ChecksTotal} passed · {ChecksWarning} warning{(ChecksWarning == 1 ? "" : "s")}";
            }
            return $"All {ChecksTotal} checks passed";
        }
    }

    /// <summary>
    /// Severity bucket for the checks InfoBar. Exposed as a string so the
    /// VM stays clear of <c>Microsoft.UI.Xaml.Controls</c>; the page
    /// converts to <c>InfoBarSeverity</c> at bind time.
    /// </summary>
    public string ChecksSeverityName
    {
        get
        {
            if (ChecksFailed > 0)
            {
                return "Error";
            }
            if (ChecksWarning > 0)
            {
                return "Warning";
            }
            return "Success";
        }
    }

    // -------- File tree + selection --------

    public ObservableCollection<DiffFileViewModel> Files { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(SelectedFilePath))]
    [NotifyPropertyChangedFor(nameof(SelectedFolder))]
    [NotifyPropertyChangedFor(nameof(SelectedFileName))]
    [NotifyPropertyChangedFor(nameof(SelectedAddDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedDelDisplay))]
    private DiffFileViewModel? _selectedFile;

    public bool HasSelectedFile => SelectedFile is not null;

    public string SelectedFilePath => SelectedFile?.Path.Value ?? string.Empty;

    public string SelectedFolder => SelectedFile?.Folder ?? string.Empty;

    public string SelectedFileName => SelectedFile?.FileName ?? string.Empty;

    public string SelectedAddDisplay => SelectedFile?.AddDisplay ?? string.Empty;

    public string SelectedDelDisplay => SelectedFile?.DelDisplay ?? string.Empty;

    // -------- Hunks (loaded for the selected file) --------

    public ObservableCollection<DiffHunkViewModel> Hunks { get; } = new();

    [ObservableProperty]
    private bool _isLoadingHunks;

    // -------- View-mode pivot --------

    /// <summary>
    /// "Unified" or "Split". The "Split" view ships as a design affordance for a
    /// future side-by-side renderer; today both render the same unified diff,
    /// matching the same pattern <c>PrPaneViewModel.ActiveTab</c> uses.
    /// </summary>
    [ObservableProperty]
    private string _viewMode = "Unified";

    // -------- Error surface --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // -------- Selection events --------

    /// <summary>Raised after <see cref="SelectFileAsync"/> finishes loading hunks.</summary>
    public event EventHandler<DiffFileViewModel>? FileSelected;

    // -------- Commands --------

    /// <summary>
    /// Drives row clicks in <c>PrFileTree</c>. The click event passes the row
    /// VM; the command swaps <see cref="SelectedFile"/> and reloads hunks.
    /// </summary>
    public IAsyncRelayCommand<DiffFileViewModel?> SelectFileCommand { get; }

    private async Task OnSelectFileCommand(DiffFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }
        await SelectFileAsync(file).ConfigureAwait(true);
    }

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
            ErrorMessage = FormatError(result.Error);
        }
    }

    private bool CanMerge() => HasPullRequest && MergeReady;

    // -------- Load --------

    /// <summary>
    /// Pull the PR + file list for <paramref name="workspaceId"/>. If
    /// <paramref name="preselect"/> is non-null and present in the file list,
    /// that file is selected (and its hunks loaded); otherwise the first file
    /// in the list is selected.
    /// </summary>
    public async Task LoadAsync(
        WorkspaceId workspaceId,
        RelativePath? preselect = null,
        CancellationToken ct = default
    )
    {
        // Cancel any in-flight load and replace its CTS, then link the caller's
        // token. If the caller cancels OR a later LoadAsync supersedes us, both
        // paths trigger OperationCanceledException out of the awaited services.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;

        _workspaceId = workspaceId;
        _prId = null;
        HasPullRequest = false;
        ErrorMessage = null;
        Files.Clear();
        Hunks.Clear();
        SelectedFile = null;
        Number = 0;
        Title = string.Empty;
        Branch = string.Empty;
        Base = string.Empty;
        Status = PrStatus.Draft;
        MergeReady = false;
        TotalAdd = 0;
        TotalDel = 0;
        FileCount = 0;
        ChecksTotal = 0;
        ChecksPassed = 0;
        ChecksFailed = 0;
        ChecksWarning = 0;

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
                CountChecks(pr.Checks);
            }
            else
            {
                // No PR is an expected state for workspaces without one; surface only
                // unexpected failures (network / auth) so the page can render an empty
                // "no PR yet" shell.
                if (!prResult.Error.IsNotFound)
                {
                    ErrorMessage = FormatError(prResult.Error);
                }
            }

            var files = await _services
                .Diffs.GetWorkspaceDiffAsync(workspaceId, token)
                .ConfigureAwait(true);
            var totalAdd = 0;
            var totalDel = 0;
            foreach (var file in files)
            {
                // PrFileTree routes clicks via ListView.ItemClick, so the row's
                // OpenCommand is unused on this surface — pass null instead of a
                // no-op delegate that would silently swallow future invocations.
                Files.Add(new DiffFileViewModel(file, onOpen: null));
                totalAdd += file.Add;
                totalDel += file.Del;
            }
            TotalAdd = totalAdd;
            TotalDel = totalDel;
            FileCount = files.Count;

            var initial = FindFileToSelect(preselect);
            if (initial is not null)
            {
                await SelectFileAsync(initial, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later LoadAsync or the caller cancelled. The
            // newer call already reset the observable state for its workspace;
            // nothing to clean up here.
        }
    }

    private DiffFileViewModel? FindFileToSelect(RelativePath? preselect)
    {
        if (Files.Count == 0)
        {
            return null;
        }
        if (preselect is RelativePath p)
        {
            foreach (var f in Files)
            {
                if (f.Path.Equals(p))
                {
                    return f;
                }
            }
        }
        return Files[0];
    }

    /// <summary>
    /// Mark <paramref name="file"/> as the active row and load its hunks via
    /// <see cref="IDiffService.GetHunksAsync"/>. Updates focus highlights so
    /// the tree row reflects the selection.
    /// </summary>
    public async Task SelectFileAsync(DiffFileViewModel file, CancellationToken ct = default)
    {
        SelectedFile = file;
        foreach (var row in Files)
        {
            row.IsFocused = ReferenceEquals(row, file);
        }
        Hunks.Clear();
        if (_prId is null)
        {
            return;
        }

        IsLoadingHunks = true;
        try
        {
            var hunks = await _services
                .Diffs.GetHunksAsync(_prId, file.Path, ct)
                .ConfigureAwait(true);
            foreach (var hunk in hunks)
            {
                Hunks.Add(new DiffHunkViewModel(hunk));
            }
        }
        finally
        {
            IsLoadingHunks = false;
        }
        FileSelected?.Invoke(this, file);
    }

    // -------- Helpers --------

    private void CountChecks(IReadOnlyList<Check> checks)
    {
        ChecksTotal = checks.Count;
        var passed = 0;
        var failed = 0;
        var warned = 0;
        foreach (var c in checks)
        {
            if (c.Status.IsPassed)
            {
                passed++;
            }
            else if (c.Status.IsFailed)
            {
                failed++;
            }
            else if (c.Status.IsWarn)
            {
                warned++;
            }
        }
        ChecksPassed = passed;
        ChecksFailed = failed;
        ChecksWarning = warned;
    }

    private static string FormatStatus(PrStatus status)
    {
        if (status.IsDraft)
            return "Draft";
        if (status.IsOpen)
            return "Open";
        if (status.IsInReview)
            return "In review";
        if (status.IsApproved)
            return "Approved";
        if (status.IsMerged)
            return "Merged";
        if (status.IsClosed)
            return "Closed";
        return string.Empty;
    }

    private static string FormatError(AveliaError error) =>
        error.Match<string>(
            onNotFound: resource => $"Not found: {resource}",
            onValidation: msg => msg,
            onUnauthorized: () => "You're not signed in.",
            onConflict: msg => msg,
            onNetwork: msg => $"Network error: {msg}",
            onInternal: msg => $"Internal error: {msg}"
        );
}
