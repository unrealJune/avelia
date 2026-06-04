using System;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Terminal;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Sticky bottom panel — tab strip + mono prompt line + blinking caret.
/// Real run output (process stdout, exit codes) flows in once
/// <c>IRunService.ObserveOutput</c> is implemented in a later chunk; the
/// XAML here keeps the seat warm with a typed VM binding.
///
/// Storyboard lifecycle: the cursor blink Storyboard is created once when
/// the cursor first loads, started, and stopped on <see cref="OnUnloaded"/>.
/// Without that we'd leak a forever-repeating animation on every page
/// navigation.
/// </summary>
public sealed partial class TerminalPanel : UserControl
{
    private Storyboard? _cursorBlinkStoryboard;
    private ITerminalLaunchService? _launcher;
    private WorkspaceId? _workspaceId;
    private TerminalView? _view;
    private TerminalBridge? _bridge;
    private IInteractiveAgentSession? _session;
    private bool _launching;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Provide the launch service + workspace so the "Open interactive terminal"
    /// button can host a live ConPTY-backed CLI. Called by the host page after
    /// the workspace loads. No-op data is fine on the stub backend (the launch
    /// fails gracefully with an error message).
    /// </summary>
    public void Configure(ITerminalLaunchService launcher, WorkspaceId workspaceId)
    {
        _launcher = launcher;
        _workspaceId = workspaceId;
    }

    private async void OnOpenTerminalClick(object sender, RoutedEventArgs e)
    {
        if (_launcher is null || _workspaceId is null || _bridge is not null || _launching)
        {
            return;
        }
        _launching = true;
        try
        {
            var result = await _launcher.StartAsync(_workspaceId, CancellationToken.None);
            if (!result.IsSuccess)
            {
                TerminalError.Text = "Couldn't start the terminal: " + DescribeError(result.Error);
                TerminalError.Visibility = Visibility.Visible;
                return;
            }

            _session = result.Value;
            _view = new TerminalView();
            TerminalHost.Child = _view;
            TerminalHost.Visibility = Visibility.Visible;
            OpenTerminalButton.Visibility = Visibility.Collapsed;
            TerminalCursor.Visibility = Visibility.Collapsed;

            await _view.Ready;
            _bridge = new TerminalBridge(_session.Terminal, _view);
            _bridge.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TerminalPanel] launch failed: {ex}");
        }
        finally
        {
            _launching = false;
        }
    }

    private static string DescribeError(AveliaError error) =>
        error.Match(
            onNotFound: r => r,
            onValidation: m => m,
            onUnauthorized: () => "not authorized",
            onConflict: m => m,
            onNetwork: m => m,
            onInternal: m => m,
            onExternal: (src, detail) => $"{src}: {detail}"
        );

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TerminalPanelViewModel),
        typeof(TerminalPanel),
        new PropertyMetadata(null)
    );

    public TerminalPanelViewModel? ViewModel
    {
        get => (TerminalPanelViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnCursorLoaded(object sender, RoutedEventArgs e)
    {
        if (_cursorBlinkStoryboard is not null || sender is not FrameworkElement cursor)
        {
            return;
        }
        var blink = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(530)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(blink, cursor);
        Storyboard.SetTargetProperty(blink, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(blink);
        sb.Begin();
        _cursorBlinkStoryboard = sb;
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop and drop the Storyboard so re-loading the control (e.g. after
        // page navigation) doesn't accumulate animations targeting a stale
        // Rectangle. Loaded fires again on re-mount and rebuilds.
        _cursorBlinkStoryboard?.Stop();
        _cursorBlinkStoryboard = null;

        // Tear down the live terminal (bridge first so it stops reading, then
        // the session which kills the ConPTY child).
        var bridge = _bridge;
        var session = _session;
        _bridge = null;
        _session = null;
        try
        {
            if (bridge is not null)
            {
                await bridge.DisposeAsync();
            }
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TerminalPanel] teardown failed: {ex}");
        }
    }

    private void OnTabSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args
    )
    {
        if (
            ViewModel is null
            || sender.SelectedItem is not SelectorBarItem item
            || item.Text is null
        )
        {
            return;
        }
        ViewModel.ActiveTab = item.Text;
    }
}
