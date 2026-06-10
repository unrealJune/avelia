using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// VM for the Agents &amp; Models subpage. Exposes the unified default model bar
/// (model · reasoning effort · context tier — see <see cref="ModelBar"/>) plus
/// the GitHub-token (Copilot auth) state. Each model-bar gesture persists the
/// matching default through <see cref="ISettingsService"/>.
/// </summary>
public partial class AgentsSubpageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    public AgentsSubpageViewModel(AveliaServices services)
    {
        _settings = services.Settings;

        ModelBar.ModelChanged = model =>
            FireAndForget(
                _settings.SetDefaultModelAsync(model, CancellationToken.None),
                nameof(_settings.SetDefaultModelAsync)
            );
        ModelBar.ReasoningChanged = effort =>
            FireAndForget(
                _settings.SetReasoningEffortAsync(effort, CancellationToken.None),
                nameof(_settings.SetReasoningEffortAsync)
            );
        ModelBar.ContextChanged = tier =>
            FireAndForget(
                _settings.SetContextTierAsync(tier, CancellationToken.None),
                nameof(_settings.SetContextTierAsync)
            );
    }

    /// <summary>
    /// Unified default picker — model, reasoning effort, and context tier. The
    /// initial selection for new conversations; the composer can override
    /// per-conversation.
    /// </summary>
    public ModelBarViewModel ModelBar { get; } = new();

    /// <summary>Bound from the PasswordBox (code-behind PasswordChanged).</summary>
    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private bool _isGitHubConnected;

    [ObservableProperty]
    private string _gitHubStatus = "Checking…";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(true);
        ModelBar.SetSelections(
            snapshot.DefaultModel,
            snapshot.ReasoningEffort,
            snapshot.ContextTier
        );
        await RefreshConnectionAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Save the pasted PAT to the credential vault, then refresh status.</summary>
    [RelayCommand]
    private async Task SaveGitHubTokenAsync()
    {
        var result = await _settings
            .SetGitHubTokenAsync(GitHubToken, CancellationToken.None)
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            GitHubToken = string.Empty; // don't keep the secret in the VM
        }
        await RefreshConnectionAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshConnectionAsync(CancellationToken ct)
    {
        IsGitHubConnected = await _settings.HasGitHubTokenAsync(ct).ConfigureAwait(true);
        GitHubStatus = IsGitHubConnected
            ? "Connected — a GitHub token is available for Copilot."
            : "Not connected — paste a GitHub token (or set COPILOT_GITHUB_TOKEN).";
    }

    private static void FireAndForget(Task task, string op)
    {
        _ = task.ContinueWith(
            t =>
                System.Diagnostics.Debug.WriteLine(
                    $"[AgentsSubpageViewModel] {op} failed: {t.Exception}"
                ),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously
        );
    }
}
