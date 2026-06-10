using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Avelia.Shell.Windows.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(Composer),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty SendCommandProperty = DependencyProperty.Register(
        nameof(SendCommand),
        typeof(ICommand),
        typeof(Composer),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty ModelBarProperty = DependencyProperty.Register(
        nameof(ModelBar),
        typeof(ModelBarViewModel),
        typeof(Composer),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty AttachmentsProperty = DependencyProperty.Register(
        nameof(Attachments),
        typeof(object),
        typeof(Composer),
        new PropertyMetadata(null)
    );

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

    public ModelBarViewModel? ModelBar
    {
        get => (ModelBarViewModel?)GetValue(ModelBarProperty);
        set => SetValue(ModelBarProperty, value);
    }

    /// <summary>
    /// Files staged to send with the next message. The owning view-model
    /// supplies an <see cref="IList"/> of <see cref="ComposerAttachmentViewModel"/>;
    /// the paste handler appends pasted images to it.
    /// </summary>
    public object? Attachments
    {
        get => GetValue(AttachmentsProperty);
        set => SetValue(AttachmentsProperty, value);
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

    /// <summary>
    /// Intercepts paste into the message box. If the clipboard holds a bitmap
    /// (e.g. a screenshot), it is re-encoded to a temp PNG and staged as an
    /// attachment instead of being dropped on the floor — a plain
    /// <c>TextBox</c> can't render an image, so the default paste would lose it.
    /// Text paste is left to the default handler.
    /// </summary>
    private async void OnInputPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView clipboard;
        try
        {
            clipboard = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            // Clipboard access can transiently fail (another app holds it open).
            Debug.WriteLine($"Composer: clipboard read failed: {ex.Message}");
            return;
        }

        if (!clipboard.Contains(StandardDataFormats.Bitmap))
        {
            // Plain text / other content — let the TextBox paste it normally.
            return;
        }

        // We're taking over this paste: stop the TextBox from also acting on it.
        e.Handled = true;

        try
        {
            var bitmapRef = await clipboard.GetBitmapAsync();
            var path = await SaveBitmapAsPngAsync(bitmapRef);
            AddAttachment(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Composer: pasting image failed: {ex.Message}");
        }
    }

    private void AddAttachment(string path)
    {
        if (Attachments is IList list)
        {
            list.Add(new ComposerAttachmentViewModel(path));
        }
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (
            sender is FrameworkElement { DataContext: ComposerAttachmentViewModel item }
            && Attachments is IList list
        )
        {
            list.Remove(item);
        }
    }

    /// <summary>Decodes a clipboard bitmap and writes it as a PNG to a temp file, returning its path.</summary>
    private static async Task<string> SaveBitmapAsPngAsync(RandomAccessStreamReference bitmapRef)
    {
        using IRandomAccessStreamWithContentType source = await bitmapRef.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        // Force BGRA8 so SetSoftwareBitmap accepts it regardless of the source format.
        var software = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied
        );

        var dir = Path.Combine(Path.GetTempPath(), "Avelia", "attachments");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"paste-{Guid.NewGuid():N}.png");

        using (var fileStream = File.Create(path))
        using (var ras = fileStream.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            encoder.SetSoftwareBitmap(software);
            await encoder.FlushAsync();
        }

        return path;
    }
}
