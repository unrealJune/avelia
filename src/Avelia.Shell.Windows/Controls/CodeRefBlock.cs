using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Hosts a <c>TextBlock</c> that renders body text with inline code-refs styled
/// distinctly. Setting <see cref="SourceText"/> reparses the string and emits
/// a sequence of <c>Run</c> elements: plain text in primary foreground (the
/// inner TextBlock's TextStyle drives this), and any token matching
/// <c>@&lt;name&gt;.&lt;ext&gt;</c> rendered in mono font with the accent-text
/// brush.
///
/// (WinUI 3 seals <c>TextBlock</c> and <c>Run.Style</c> doesn't exist, so we
/// can't decorate refs with a Style. Instead, we subscribe to
/// <c>ActualThemeChanged</c> on the host control and re-resolve the accent
/// brush from the merged ThemeDictionaries each rebuild. This keeps inline
/// refs theme-correct without freezing a brush at first paint.)
/// </summary>
public sealed class CodeRefBlock : UserControl
{
    private static readonly Regex CodeRefRegex =
        new(@"(?<=^|\s)@([A-Za-z0-9_\-.]+\.[A-Za-z0-9]+)", RegexOptions.Compiled);

    private const string AccentBrushKey = "AveliaAccentTextBrush";
    private const string MonoFontFamilyKey = "AveliaMonoFontFamily";

    private readonly TextBlock _text;

    public CodeRefBlock()
    {
        IsTabStop = false;
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Content = _text;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(
            nameof(SourceText),
            typeof(string),
            typeof(CodeRefBlock),
            new PropertyMetadata(string.Empty, OnSourceTextChanged));

    public static readonly DependencyProperty TextStyleProperty =
        DependencyProperty.Register(
            nameof(TextStyle),
            typeof(Style),
            typeof(CodeRefBlock),
            new PropertyMetadata(null, OnTextStyleChanged));

    public static readonly DependencyProperty TextWrappingProperty =
        DependencyProperty.Register(
            nameof(TextWrapping),
            typeof(TextWrapping),
            typeof(CodeRefBlock),
            new PropertyMetadata(TextWrapping.Wrap, OnTextWrappingChanged));

    public static readonly DependencyProperty TextForegroundProperty =
        DependencyProperty.Register(
            nameof(TextForeground),
            typeof(Brush),
            typeof(CodeRefBlock),
            new PropertyMetadata(null, OnTextForegroundChanged));

    /// <summary>The raw text to render with code-refs highlighted.</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>Style applied to the inner TextBlock (<c>TargetType="TextBlock"</c>).</summary>
    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    /// <summary>Foreground brush applied to non-ref runs (overrides the TextStyle).</summary>
    public Brush? TextForeground
    {
        get => (Brush?)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeRefBlock self)
        {
            self.Rebuild();
        }
    }

    private static void OnTextStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeRefBlock self && e.NewValue is Style s)
        {
            self._text.Style = s;
        }
    }

    private static void OnTextWrappingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeRefBlock self)
        {
            self._text.TextWrapping = (TextWrapping)e.NewValue;
        }
    }

    private static void OnTextForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeRefBlock self && e.NewValue is Brush b)
        {
            self._text.Foreground = b;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Theme flipped — re-resolve the accent brush and rebuild the inlines.
        Rebuild();
    }

    /// <summary>
    /// Resolve a resource keyed for the host's current <see cref="FrameworkElement.ActualTheme"/>.
    /// Walks <see cref="Application.Current"/>'s merged dictionaries because our
    /// theme tokens live in <c>Tokens.xaml</c>, which is a merged dictionary
    /// holding <c>ThemeDictionaries</c>. Plain <c>Application.Current.Resources[key]</c>
    /// would resolve against <see cref="Application.RequestedTheme"/> only — set
    /// at startup, never updated, so it freezes the brush on the original theme.
    /// </summary>
    private object? ResolveThemed(string key)
    {
        var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Default";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var td)
                && td is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var v))
            {
                return v;
            }
        }
        // Fallback to the top-level dictionary (theme-independent resources
        // like AveliaMonoFontFamily live here).
        return Application.Current.Resources.TryGetValue(key, out var top) ? top : null;
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();
        var src = SourceText ?? string.Empty;
        if (src.Length == 0)
        {
            return;
        }

        var mono = ResolveThemed(MonoFontFamilyKey) as FontFamily;
        var accent = ResolveThemed(AccentBrushKey) as Brush;

        var idx = 0;
        foreach (Match match in CodeRefRegex.Matches(src))
        {
            if (match.Index > idx)
            {
                _text.Inlines.Add(new Run { Text = src.Substring(idx, match.Index - idx) });
            }
            var refRun = new Run { Text = match.Value };
            if (mono is not null)
            {
                refRun.FontFamily = mono;
            }
            if (accent is not null)
            {
                refRun.Foreground = accent;
            }
            _text.Inlines.Add(refRun);
            idx = match.Index + match.Length;
        }
        if (idx < src.Length)
        {
            _text.Inlines.Add(new Run { Text = src.Substring(idx) });
        }
    }
}
