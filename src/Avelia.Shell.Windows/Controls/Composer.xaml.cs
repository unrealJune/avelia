using System.Windows.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using CoreVirtualKeyStates = global::Windows.UI.Core.CoreVirtualKeyStates;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Multi-line composer with a toolbar row. Plain Enter submits via the bound
/// command (matches Conductor's keybinding); Shift+Enter inserts a newline.
/// Cancel keys, IME composition, and accessibility names are handled here so
/// the consuming page just supplies <see cref="Text"/> and <see cref="SendCommand"/>.
/// </summary>
public sealed partial class Composer : UserControl
{
    public Composer()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(Composer),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SendCommandProperty =
        DependencyProperty.Register(
            nameof(SendCommand),
            typeof(ICommand),
            typeof(Composer),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ModelNameProperty =
        DependencyProperty.Register(
            nameof(ModelName),
            typeof(string),
            typeof(Composer),
            new PropertyMetadata("Sonnet 4.5"));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? SendCommand
    {
        get => (ICommand?)GetValue(SendCommandProperty);
        set => SetValue(SendCommandProperty, value);
    }

    public string ModelName
    {
        get => (string)GetValue(ModelNameProperty);
        set => SetValue(ModelNameProperty, value);
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var shift = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (shift)
        {
            // Let the TextBox handle the newline insertion.
            return;
        }
        if (SendCommand?.CanExecute(null) != true)
        {
            // Composer empty / disabled — leave the keystroke alone so the
            // TextBox can still beep / handle it normally rather than
            // swallowing the user's input silently.
            return;
        }
        e.Handled = true;
        SendCommand.Execute(null);
    }
}
