using System;
using Avelia.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

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
/// signal the agent is mid-run. This replaces a 14×14 <c>ProgressRing</c> overlay
/// that rendered invisibly at that size (its Lottie visual needs a larger box),
/// which left the "working" state looking identical to the static dot.
/// </summary>
public sealed partial class StatusDot : UserControl
{
    private Storyboard? _pulse;

    public StatusDot()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyStatusVisualState();
            UpdatePulse();
        };
        Unloaded += (_, _) => _pulse?.Stop();
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
        if (IsBusy)
        {
            EnsurePulse();
            _pulse!.Begin();
        }
        else
        {
            _pulse?.Stop();
            Dot.Opacity = 1.0;
        }
    }

    private void EnsurePulse()
    {
        if (_pulse is not null)
        {
            return;
        }

        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.25,
            Duration = new Duration(TimeSpan.FromMilliseconds(650)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, Dot);
        Storyboard.SetTargetProperty(anim, "Opacity");

        _pulse = new Storyboard();
        _pulse.Children.Add(anim);
    }
}
