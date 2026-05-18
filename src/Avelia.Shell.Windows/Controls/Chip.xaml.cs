using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Pill-shaped chip: small label with tinted fill + stroke. The XAML template
/// initializes the inner Border / TextBlock brushes via <c>ThemeResource</c>,
/// so the chip tracks Light↔Dark switches with no code involvement. Consumers
/// override via <see cref="FillBrush"/>, <see cref="StrokeBrush"/>, and
/// <see cref="TextBrush"/> for diff-add / diff-del / status variants — those
/// brushes are *also* expected to be <c>{ThemeResource ...}</c> references at
/// the call site so the override stays theme-aware.
/// </summary>
public sealed partial class Chip : UserControl
{
    public Chip()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(Chip),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(Chip),
        new PropertyMetadata(null, OnFillBrushChanged)
    );

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush),
        typeof(Brush),
        typeof(Chip),
        new PropertyMetadata(null, OnStrokeBrushChanged)
    );

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush),
        typeof(Brush),
        typeof(Chip),
        new PropertyMetadata(null, OnTextBrushChanged)
    );

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? StrokeBrush
    {
        get => (Brush?)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    private static void OnFillBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Chip self && e.NewValue is Brush b)
        {
            self.ChipBorder.Background = b;
        }
    }

    private static void OnStrokeBrushChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is Chip self && e.NewValue is Brush b)
        {
            self.ChipBorder.BorderBrush = b;
        }
    }

    private static void OnTextBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Chip self && e.NewValue is Brush b)
        {
            self.ChipText.Foreground = b;
        }
    }
}
