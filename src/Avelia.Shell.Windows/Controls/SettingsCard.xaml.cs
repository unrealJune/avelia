using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Settings list card — icon + title + description on the left, action slot on
/// the right. Used by every subpage in <c>Pages/SettingsSubpages</c>. The
/// Action slot accepts any FrameworkElement (segmented control, toggle switch,
/// button, swatch row, etc.).
/// </summary>
public sealed partial class SettingsCard : UserControl
{
    public SettingsCard()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action),
        typeof(object),
        typeof(SettingsCard),
        new PropertyMetadata(null)
    );

    /// <summary>Segoe Fluent Icons PUA glyph (e.g. "").</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// Trailing action element. Set in XAML as a child element of the
    /// SettingsCard tag — e.g. <c>&lt;ToggleSwitch /&gt;</c> or a
    /// <c>StackPanel</c> of swatches.
    /// </summary>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }
}
