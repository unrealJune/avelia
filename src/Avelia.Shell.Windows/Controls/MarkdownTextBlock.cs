using System;
using System.Collections.Generic;
using Avelia.Shell.Windows.Controls.Markdown;
using Avelia.Shell.Windows.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Text;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// Renders a markdown string into the chat transcript. Setting
/// <see cref="SourceText"/> reparses through <see cref="MarkdownParser"/> and
/// rebuilds a stack of selectable <c>RichTextBlock</c>s plus bordered code
/// blocks. Headings, bold / italic, inline code, links, bullet / numbered
/// lists, and Avelia's <c>@file.ext</c> code-refs are supported.
///
/// Text selection is enabled on every surface (native Ctrl+C copy), and the
/// control carries a right-click "Copy" flyout that copies the whole raw
/// message. Theme-keyed brushes are re-resolved on <c>ActualThemeChanged</c>
/// (mirroring <see cref="CodeRefBlock"/>) so inline code / links / refs stay
/// theme-correct after a runtime theme flip.
/// </summary>
public sealed class MarkdownTextBlock : UserControl
{
    private const string AccentBrushKey = "AveliaAccentTextBrush";
    private const string PrimaryBrushKey = "AveliaTextPrimaryBrush";
    private const string TertiaryBrushKey = "AveliaTextTertiaryBrush";
    private const string MonoFontFamilyKey = "AveliaMonoFontFamily";
    private const string UiFontFamilyKey = "AveliaUiFontFamily";
    private const string CodeBackgroundKey = "AveliaSubtleFillTertiaryBrush";
    private const string CodeStrokeKey = "AveliaSurfaceStrokeBrush";

    private const double BodyFontSize = 14;
    private const double BodyLineHeight = 20;

    private readonly StackPanel _root;

    public MarkdownTextBlock()
    {
        IsTabStop = false;
        _root = new StackPanel { Spacing = 8 };
        Content = _root;

        var copyItem = new MenuFlyoutItem { Text = "Copy" };
        copyItem.Click += (_, _) => CopyToClipboard();
        ContextFlyout = new MenuFlyout { Items = { copyItem } };

        ActualThemeChanged += (_, _) => Rebuild();
    }

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(string.Empty, OnSourceTextChanged)
    );

    /// <summary>The raw markdown to render.</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground),
        typeof(Brush),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(null, OnSourceTextChanged)
    );

    /// <summary>Optional override for the default body foreground (e.g. error red).</summary>
    public Brush? TextForeground
    {
        get => (Brush?)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    private static void OnSourceTextChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is MarkdownTextBlock self)
        {
            self.Rebuild();
        }
    }

    private void CopyToClipboard()
    {
        var text = SourceText ?? string.Empty;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Rebuild()
    {
        _root.Children.Clear();

        var doc = MarkdownParser.Parse(SourceText);
        if (doc.Blocks.Count == 0)
        {
            return;
        }

        var mono = ThemeResources.Resolve(this, MonoFontFamilyKey) as FontFamily;
        var ui = ThemeResources.Resolve(this, UiFontFamilyKey) as FontFamily;
        var accent = ThemeResources.Resolve(this, AccentBrushKey) as Brush;
        var primary = TextForeground ?? ThemeResources.Resolve(this, PrimaryBrushKey) as Brush;
        var tertiary = ThemeResources.Resolve(this, TertiaryBrushKey) as Brush;
        var codeBg = ThemeResources.Resolve(this, CodeBackgroundKey) as Brush;
        var codeStroke = ThemeResources.Resolve(this, CodeStrokeKey) as Brush;
        var ctx = new RenderContext(mono, ui, accent, primary, tertiary);

        foreach (var block in doc.Blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    _root.Children.Add(BuildHeading(block, ctx));
                    break;
                case MarkdownBlockKind.CodeBlock:
                    _root.Children.Add(BuildCodeBlock(block, ctx, codeBg, codeStroke));
                    break;
                case MarkdownBlockKind.BulletList:
                case MarkdownBlockKind.NumberedList:
                    _root.Children.Add(BuildList(block, ctx));
                    break;
                default:
                    _root.Children.Add(
                        BuildParagraph(block.Inlines, ctx, BodyFontSize, FontWeights.Normal)
                    );
                    break;
            }
        }
    }

    private sealed record RenderContext(
        FontFamily? Mono,
        FontFamily? Ui,
        Brush? Accent,
        Brush? Primary,
        Brush? Tertiary
    );

    private RichTextBlock BuildHeading(MarkdownBlock block, RenderContext ctx)
    {
        var size = block.HeadingLevel switch
        {
            1 => 20.0,
            2 => 18.0,
            3 => 16.0,
            _ => 14.0,
        };
        return BuildParagraph(block.Inlines, ctx, size, FontWeights.SemiBold);
    }

    private RichTextBlock BuildParagraph(
        IReadOnlyList<MarkdownInline> inlines,
        RenderContext ctx,
        double fontSize,
        FontWeight weight
    )
    {
        var rtb = NewRichTextBlock(ctx, fontSize, weight);
        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, inlines, ctx);
        rtb.Blocks.Add(paragraph);
        return rtb;
    }

    private UIElement BuildList(MarkdownBlock block, RenderContext ctx)
    {
        var panel = new StackPanel { Spacing = 3 };
        var numbered = block.Kind == MarkdownBlockKind.NumberedList;
        foreach (var item in block.Items)
        {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            );

            var marker = new TextBlock
            {
                Text = numbered ? $"{item.Number}." : "•",
                FontSize = BodyFontSize,
                LineHeight = BodyLineHeight,
                Foreground = ctx.Tertiary,
                FontFamily = ctx.Ui,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var body = BuildParagraph(item.Inlines, ctx, BodyFontSize, FontWeights.Normal);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            panel.Children.Add(row);
        }
        return panel;
    }

    private UIElement BuildCodeBlock(
        MarkdownBlock block,
        RenderContext ctx,
        Brush? background,
        Brush? stroke
    )
    {
        var rtb = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            FontFamily = ctx.Mono,
            FontSize = 13,
            Foreground = ctx.Primary,
            TextWrapping = TextWrapping.Wrap,
        };
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = block.CodeText });
        rtb.Blocks.Add(paragraph);

        return new Border
        {
            Background = background,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Child = rtb,
        };
    }

    private RichTextBlock NewRichTextBlock(RenderContext ctx, double fontSize, FontWeight weight) =>
        new()
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = ctx.Ui,
            FontSize = fontSize,
            LineHeight = fontSize <= BodyFontSize ? BodyLineHeight : 0,
            FontWeight = weight,
            Foreground = ctx.Primary,
        };

    private void AppendInlines(
        InlineCollection target,
        IReadOnlyList<MarkdownInline> inlines,
        RenderContext ctx
    )
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Bold:
                    var bold = new Bold();
                    bold.Inlines.Add(new Run { Text = inline.Text });
                    target.Add(bold);
                    break;
                case MarkdownInlineKind.Italic:
                    var italic = new Italic();
                    italic.Inlines.Add(new Run { Text = inline.Text });
                    target.Add(italic);
                    break;
                case MarkdownInlineKind.Code:
                case MarkdownInlineKind.CodeRef:
                    var code = new Run { Text = inline.Text };
                    if (ctx.Mono is not null)
                    {
                        code.FontFamily = ctx.Mono;
                    }
                    if (ctx.Accent is not null)
                    {
                        code.Foreground = ctx.Accent;
                    }
                    target.Add(code);
                    break;
                case MarkdownInlineKind.Link:
                    target.Add(BuildLink(inline, ctx));
                    break;
                default:
                    target.Add(new Run { Text = inline.Text });
                    break;
            }
        }
    }

    private Microsoft.UI.Xaml.Documents.Inline BuildLink(MarkdownInline inline, RenderContext ctx)
    {
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(
            new Run { Text = string.IsNullOrEmpty(inline.Text) ? inline.Url : inline.Text }
        );
        if (Uri.TryCreate(inline.Url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }
        return hyperlink;
    }
}
