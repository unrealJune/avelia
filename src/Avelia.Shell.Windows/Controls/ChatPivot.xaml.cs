using System.Collections.Generic;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Pivot-style strip showing conversation threads in a workspace. Single
/// thread per workspace today; the strip ships now so the design's underline
/// affordance is in place ahead of multi-thread support.
/// </summary>
public sealed partial class ChatPivot : UserControl
{
    public ChatPivot()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ThreadsProperty = DependencyProperty.Register(
        nameof(Threads),
        typeof(IReadOnlyList<ChatThreadViewModel>),
        typeof(ChatPivot),
        new PropertyMetadata(null)
    );

    public IReadOnlyList<ChatThreadViewModel>? Threads
    {
        get => (IReadOnlyList<ChatThreadViewModel>?)GetValue(ThreadsProperty);
        set => SetValue(ThreadsProperty, value);
    }

    public static readonly DependencyProperty ActiveThreadProperty = DependencyProperty.Register(
        nameof(ActiveThread),
        typeof(ChatThreadViewModel),
        typeof(ChatPivot),
        new PropertyMetadata(null)
    );

    public ChatThreadViewModel? ActiveThread
    {
        get => (ChatThreadViewModel?)GetValue(ActiveThreadProperty);
        set => SetValue(ActiveThreadProperty, value);
    }

    private void OnThreadButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatThreadViewModel thread })
        {
            // The TwoWay binding on ActiveThread propagates the selection
            // back to the workspace VM — no separate ThreadSelected event
            // needed (and no consumer wires one today).
            ActiveThread = thread;
        }
    }
}
