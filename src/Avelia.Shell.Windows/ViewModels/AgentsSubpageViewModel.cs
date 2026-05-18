using System.Collections.ObjectModel;
using System.Threading;
using Avelia.Core;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Task = System.Threading.Tasks.Task;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// VM for the Agents &amp; Models subpage. Lists the three known Claude models
/// the design exposes (Sonnet 4.5 default, Opus 4.1, Haiku 4.5) and the
/// extended-thinking toggle. Each item's <see cref="AgentModelOption.IsSelected"/>
/// flag is mutated on <see cref="SelectedModel"/> change so the XAML can bind
/// <c>RadioButton.IsChecked</c> two-way without any visual-tree walks.
/// </summary>
public partial class AgentsSubpageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _isLoading;

    public AgentsSubpageViewModel(AveliaServices services)
    {
        _settings = services.Settings;

        Models.Add(
            new AgentModelOption(
                ModelChoice.Sonnet45,
                "Sonnet 4.5",
                "Balanced — fastest default for most agent runs.",
                onChecked: SelectFromRadio
            )
        );
        Models.Add(
            new AgentModelOption(
                ModelChoice.Opus41,
                "Opus 4.1",
                "Most capable — pick this for tricky refactors and long contexts.",
                onChecked: SelectFromRadio
            )
        );
        Models.Add(
            new AgentModelOption(
                ModelChoice.Haiku45,
                "Haiku 4.5",
                "Lightweight — quickest token-throughput, smallest answers.",
                onChecked: SelectFromRadio
            )
        );
    }

    public ObservableCollection<AgentModelOption> Models { get; } = new();

    [ObservableProperty]
    private AgentModelOption? _selectedModel;

    [ObservableProperty]
    private bool _extendedThinking;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(true);
        _isLoading = true;
        try
        {
            ExtendedThinking = snapshot.ExtendedThinking;
            SelectedModel = FindModel(snapshot.DefaultModel);
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedModelChanged(AgentModelOption? value)
    {
        // Keep each item's IsSelected flag in sync with the VM-level selection
        // so the radio TwoWay binding visibly reflects external updates
        // (e.g. LoadAsync hydrating the persisted default).
        foreach (var m in Models)
        {
            m.SetSelectedFromOwner(ReferenceEquals(m, value));
        }
        if (_isLoading || value is null)
            return;
        FireAndForget(
            _settings.SetDefaultModelAsync(value.Choice, CancellationToken.None),
            nameof(_settings.SetDefaultModelAsync)
        );
    }

    partial void OnExtendedThinkingChanged(bool value)
    {
        if (_isLoading)
            return;
        FireAndForget(
            _settings.SetExtendedThinkingAsync(value, CancellationToken.None),
            nameof(_settings.SetExtendedThinkingAsync)
        );
    }

    [RelayCommand]
    private void SelectModel(AgentModelOption option) => SelectedModel = option;

    private void SelectFromRadio(AgentModelOption option)
    {
        if (_isLoading)
            return;
        // The radio's checked-state is the source of truth for user gestures;
        // this routes back through the standard SelectedModel setter so the
        // persistence path runs once.
        SelectedModel = option;
    }

    private AgentModelOption? FindModel(ModelChoice choice)
    {
        foreach (var m in Models)
        {
            if (Equals(m.Choice, choice))
            {
                return m;
            }
        }
        return null;
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

/// <summary>
/// One model card on the Agents subpage. <see cref="IsSelected"/> is bound
/// <c>TwoWay</c> to the row's <c>RadioButton.IsChecked</c>: a user click sets
/// it <c>true</c> and triggers the owner callback that updates the VM-level
/// <c>SelectedModel</c>; an external change (e.g. <c>LoadAsync</c>) feeds the
/// new value via <see cref="SetSelectedFromOwner"/> without firing the callback.
/// </summary>
public partial class AgentModelOption : ObservableObject
{
    private readonly System.Action<AgentModelOption>? _onChecked;
    private bool _suppressCallback;

    public AgentModelOption(
        ModelChoice choice,
        string displayName,
        string description,
        System.Action<AgentModelOption>? onChecked = null
    )
    {
        Choice = choice;
        DisplayName = displayName;
        Description = description;
        _onChecked = onChecked;
    }

    public ModelChoice Choice { get; }
    public string DisplayName { get; }
    public string Description { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_suppressCallback || !value)
            return;
        _onChecked?.Invoke(this);
    }

    /// <summary>
    /// Owner-side update: flips <see cref="IsSelected"/> without re-firing the
    /// owner callback (which would otherwise loop back into the setter that
    /// triggered this call).
    /// </summary>
    internal void SetSelectedFromOwner(bool value)
    {
        if (IsSelected == value)
            return;
        _suppressCallback = true;
        try
        {
            IsSelected = value;
        }
        finally
        {
            _suppressCallback = false;
        }
    }
}
