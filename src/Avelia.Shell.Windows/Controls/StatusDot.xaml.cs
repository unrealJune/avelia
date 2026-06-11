using Avelia.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Small colored ellipse rendering a <see cref="WorkspaceStatus"/>. Used by
/// the tab strip and the nav-rail workspace tree. The fill is applied through
/// visual states (see <c>StatusDot.xaml</c>) so the brush resolves against the
/// control's live theme rather than a converter reading
/// <c>Application.Current.Resources</c> (which can't see brushes nested in a
/// merged dictionary's ThemeDictionaries, and so always rendered grey).
///
/// When <see cref="IsBusy"/> is set the dot pulses (opacity fade in/out) to
/// signal the agent is mid-run. The pulse is a VSM-managed storyboard
/// (<c>PulsingActivity</c> in <c>StatusDot.xaml</c>) rather than a hand-rolled
/// <c>Storyboard.Begin()</c>: the framework re-asserts the active state when the
/// dot is recycled by the tab/rail list, so the pulse keeps running for the
/// whole turn instead of dropping to solid amber after the first streamed
/// events. It replaces a 14×14 <c>ProgressRing</c> overlay that rendered
/// invisibly at that size (its Lottie visual needs a larger box).
/// </summary>
public sealed partial class StatusDot : UserControl
{
    public StatusDot()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyStatusVisualState();
            UpdatePulse();
        };
    }

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(WorkspaceStatus),
        typeof(StatusDot),
        new PropertyMetadata(WorkspaceStatus.Draft, OnStatusChanged)
    );

    public WorkspaceStatus Status
    {
        get => (WorkspaceStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>
    /// The workspace's agent is actively running. While true the dot pulses so
    /// the "working" state is visually distinct from a settled (static) dot.
    /// </summary>
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy),
        typeof(bool),
        typeof(StatusDot),
        new PropertyMetadata(false, OnIsBusyChanged)
    );

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusDot)d).ApplyStatusVisualState();

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusDot)d).UpdatePulse();

    private void ApplyStatusVisualState()
    {
        var state =
            Status.IsActive ? "ActiveState"
            : Status.IsWorking ? "WorkingState"
            : Status.IsReady ? "ReadyState"
            : Status.IsConflict ? "ConflictState"
            : Status.IsOpen ? "OpenState"
            : Status.IsArchived ? "ArchivedState"
            : "DraftState";
        VisualStateManager.GoToState(this, state, false);
    }

    private void UpdatePulse()
    {
        // Reset the group first so re-entering PulsingActivity restarts the
        // storyboard even when the VSM still considers it the active state (e.g.
        // after the dot is recycled by the tab/rail list, where the state sticks
        // but the storyboard has been torn down). Without the reset, a no-op
        // GoToState would leave the dot frozen on solid amber.
        VisualStateManager.GoToState(this, "StaticActivity", false);
        if (IsBusy)
        {
            VisualStateManager.GoToState(this, "PulsingActivity", false);
        }
    }
}
