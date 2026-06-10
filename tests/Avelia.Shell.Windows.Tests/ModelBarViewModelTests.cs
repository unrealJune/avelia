using System.Collections.Generic;
using System.Linq;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.ViewModels;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

public class ModelBarViewModelTests
{
    private static ModelInfo Model(string id, string display, params string[] efforts) =>
        new(id, display, "", efforts);

    [Fact]
    public void SetCatalog_DrivesThinkingDropdownFromModelSupportedEfforts()
    {
        var vm = new ModelBarViewModel();
        var sonnet = Model(ModelCatalog.SonnetId, "Sonnet 4.5", "high", "max");
        vm.SetCatalog(new[] { sonnet });
        vm.SetSelections(ModelChoice.Sonnet45, ReasoningEffort.High, ContextTier.Default);

        var labels = vm.ReasoningOptions.Select(o => ((ReasoningEffort)o.Value).ApiValue).ToArray();
        Assert.Equal(new[] { "high", "max" }, labels);
    }

    [Fact]
    public void SetCatalog_EmptyEfforts_FallsBackToFullStaticList()
    {
        var vm = new ModelBarViewModel();
        var sonnet = Model(ModelCatalog.SonnetId, "Sonnet 4.5"); // no efforts
        vm.SetCatalog(new[] { sonnet });
        vm.SetSelections(ModelChoice.Sonnet45, ReasoningEffort.High, ContextTier.Default);

        Assert.Equal(ReasoningEffort.All.Length, vm.ReasoningOptions.Count);
    }

    [Fact]
    public void SelectingDifferentModel_RepointsThinkingDropdown()
    {
        ReasoningEffort? notified = null;
        var vm = new ModelBarViewModel { ReasoningChanged = e => notified = e };
        vm.SetCatalog(
            new[]
            {
                Model(ModelCatalog.SonnetId, "Sonnet 4.5", "none", "high", "xhigh", "max"),
                Model(ModelCatalog.HaikuId, "Haiku 4.5", "none", "high"),
            }
        );
        vm.SetSelections(ModelChoice.Sonnet45, ReasoningEffort.Max, ContextTier.Default);
        Assert.Equal(4, vm.ReasoningOptions.Count);

        var haiku = vm.ModelOptions.First(o => o.Value.Equals(ModelChoice.Haiku45));
        vm.SelectedModel = haiku;

        var tokens = vm.ReasoningOptions.Select(o => ((ReasoningEffort)o.Value).ApiValue).ToArray();
        Assert.Equal(new[] { "none", "high" }, tokens);
        // Max isn't supported by Haiku, so the selection drops to the first option.
        Assert.Equal(ReasoningEffort.Off, vm.SelectedReasoning!.Value);
    }
}
