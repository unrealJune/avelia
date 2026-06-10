using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avelia.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Avelia.Shell.Windows.ViewModels;

/// <summary>
/// One option in a <see cref="ModelBarViewModel"/> dropdown. <see cref="Display"/>
/// is the label the ComboBox renders (via <c>DisplayMemberPath</c>);
/// <see cref="Value"/> is the backing domain value (<c>ModelChoice</c>,
/// <c>ReasoningEffort</c>, or <c>ContextTier</c>). Selection matching relies on
/// the domain values' structural equality, so the option instance itself can be
/// looked up from its collection.
/// </summary>
public sealed class ModelBarOption
{
    public ModelBarOption(string display, object value)
    {
        Display = display;
        Value = value;
    }

    public string Display { get; }
    public object Value { get; }

    public override string ToString() => Display;
}

/// <summary>
/// Backs the unified composer / settings "model bar": three dropdowns — model,
/// reasoning effort, and context tier — in a single control (mirrors VS Code's
/// combined model picker). Hosts wire the three <c>*Changed</c> callbacks to
/// persist or apply a user gesture; programmatic seeding via
/// <see cref="SetSelections"/> suppresses those callbacks.
/// </summary>
public partial class ModelBarViewModel : ObservableObject
{
    private static readonly ModelChoice[] PresetModels =
    {
        ModelChoice.Sonnet45,
        ModelChoice.Opus41,
        ModelChoice.Haiku45,
    };

    private bool _suppress;

    public ModelBarViewModel()
    {
        foreach (var m in PresetModels)
        {
            ModelOptions.Add(new ModelBarOption(FormatModel(m), m));
        }
        foreach (var e in ReasoningEffort.All)
        {
            ReasoningOptions.Add(new ModelBarOption(e.Label, e));
        }
        foreach (var t in ContextTier.All)
        {
            ContextOptions.Add(new ModelBarOption(t.Label, t));
        }
    }

    public ObservableCollection<ModelBarOption> ModelOptions { get; } = new();
    public ObservableCollection<ModelBarOption> ReasoningOptions { get; } = new();
    public ObservableCollection<ModelBarOption> ContextOptions { get; } = new();

    /// <summary>
    /// Replace the model dropdown with the live Copilot catalog. Each
    /// <see cref="ModelInfo"/> id is mapped back to a <see cref="ModelChoice"/>
    /// (a catalog id outside the three presets becomes a <c>CustomModel</c>), so
    /// the picked value still round-trips through the persisted setting. A blank
    /// or empty catalog is ignored, leaving the built-in presets in place so the
    /// dropdown is never empty. Does not fire the change callbacks; callers
    /// typically follow with <see cref="SetSelections"/>.
    /// </summary>
    public void SetCatalog(IReadOnlyList<ModelInfo> models)
    {
        if (models is null || models.Count == 0)
        {
            return;
        }

        _suppress = true;
        try
        {
            ModelOptions.Clear();
            foreach (var m in models)
            {
                var choice = ModelCatalog.ChoiceOfId(m.Id);
                var display = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName;
                ModelOptions.Add(new ModelBarOption(display, choice));
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    [ObservableProperty]
    private ModelBarOption? _selectedModel;

    [ObservableProperty]
    private ModelBarOption? _selectedReasoning;

    [ObservableProperty]
    private ModelBarOption? _selectedContext;

    /// <summary>Raised when the user picks a model. Not raised by <see cref="SetSelections"/>.</summary>
    public Action<ModelChoice>? ModelChanged { get; set; }

    /// <summary>Raised when the user picks a reasoning effort.</summary>
    public Action<ReasoningEffort>? ReasoningChanged { get; set; }

    /// <summary>Raised when the user picks a context tier.</summary>
    public Action<ContextTier>? ContextChanged { get; set; }

    /// <summary>
    /// Seed all three selections without firing the change callbacks. A model
    /// outside the preset list (e.g. a <c>CustomModel</c>) is appended so the
    /// ComboBox can still render and select it.
    /// </summary>
    public void SetSelections(ModelChoice model, ReasoningEffort effort, ContextTier tier)
    {
        _suppress = true;
        try
        {
            SelectedModel = FindOrAddModel(model);
            SelectedReasoning = ReasoningOptions.First(o => o.Value.Equals(effort));
            SelectedContext = ContextOptions.First(o => o.Value.Equals(tier));
        }
        finally
        {
            _suppress = false;
        }
    }

    private ModelBarOption FindOrAddModel(ModelChoice model)
    {
        var existing = ModelOptions.FirstOrDefault(o => o.Value.Equals(model));
        if (existing is not null)
        {
            return existing;
        }
        var added = new ModelBarOption(FormatModel(model), model);
        ModelOptions.Add(added);
        return added;
    }

    partial void OnSelectedModelChanged(ModelBarOption? value)
    {
        if (_suppress || value is null)
            return;
        ModelChanged?.Invoke((ModelChoice)value.Value);
    }

    partial void OnSelectedReasoningChanged(ModelBarOption? value)
    {
        if (_suppress || value is null)
            return;
        ReasoningChanged?.Invoke((ReasoningEffort)value.Value);
    }

    partial void OnSelectedContextChanged(ModelBarOption? value)
    {
        if (_suppress || value is null)
            return;
        ContextChanged?.Invoke((ContextTier)value.Value);
    }

    /// <summary>Short display name for a model choice (e.g. "Sonnet 4.5").</summary>
    public static string FormatModel(ModelChoice agent) =>
        agent.Match<string>(
            sonnet45: () => "Sonnet 4.5",
            opus41: () => "Opus 4.1",
            haiku45: () => "Haiku 4.5",
            custom: name => name
        );
}
