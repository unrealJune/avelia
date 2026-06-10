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
/// </summary>
public sealed partial class StatusDot : UserControl
{
    public StatusDot()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyStatusVisualState();
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

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusDot)d).ApplyStatusVisualState();

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
}
