using System.Collections.Generic;

namespace Avelia.Shell.Windows.Controls.Markdown;

/// <summary>The block-level shape of one parsed markdown element.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    CodeBlock,
    BulletList,
    NumberedList,
}

/// <summary>The inline-level shape of a styled text run.</summary>
public enum MarkdownInlineKind
{
    /// <summary>Plain text, primary foreground.</summary>
    Text,

    /// <summary>Bold (<c>**x**</c> / <c>__x__</c>).</summary>
    Bold,

    /// <summary>Italic (<c>*x*</c> / <c>_x_</c>).</summary>
    Italic,

    /// <summary>Inline code span (<c>`x`</c>) — mono font, accent foreground.</summary>
    Code,

    /// <summary>Markdown link (<c>[text](url)</c>).</summary>
    Link,

    /// <summary>An <c>@file.ext</c> code-reference, highlighted like inline code.</summary>
    CodeRef,
}

/// <summary>
/// One styled run inside a paragraph, heading, or list item. Inlines are flat:
/// bold/italic/code/link content is plain text (no nested formatting), which
/// covers the formatting agents emit while keeping the renderer simple.
/// </summary>
public sealed class MarkdownInline
{
    public MarkdownInline(MarkdownInlineKind kind, string text, string url = "")
    {
        Kind = kind;
        Text = text;
        Url = url;
    }

    public MarkdownInlineKind Kind { get; }

    /// <summary>The visible text of the run.</summary>
    public string Text { get; }

    /// <summary>Target URL — populated for <see cref="MarkdownInlineKind.Link"/> only.</summary>
    public string Url { get; }
}

/// <summary>One entry in a bullet or numbered list.</summary>
public sealed class MarkdownListItem
{
    public MarkdownListItem(IReadOnlyList<MarkdownInline> inlines, int number)
    {
        Inlines = inlines;
        Number = number;
    }

    public IReadOnlyList<MarkdownInline> Inlines { get; }

    /// <summary>1-based ordinal for numbered lists; ignored for bullet lists.</summary>
    public int Number { get; }
}

/// <summary>
/// One parsed markdown block. A block is either text-bearing (paragraph /
/// heading, via <see cref="Inlines"/>), a fenced code block (via
/// <see cref="CodeText"/> / <see cref="CodeLanguage"/>), or a list (via
/// <see cref="Items"/>). The renderer switches on <see cref="Kind"/>.
/// </summary>
public sealed class MarkdownBlock
{
    public MarkdownBlock(
        MarkdownBlockKind kind,
        IReadOnlyList<MarkdownInline>? inlines = null,
        IReadOnlyList<MarkdownListItem>? items = null,
        int headingLevel = 0,
        string codeText = "",
        string codeLanguage = ""
    )
    {
        Kind = kind;
        Inlines = inlines ?? System.Array.Empty<MarkdownInline>();
        Items = items ?? System.Array.Empty<MarkdownListItem>();
        HeadingLevel = headingLevel;
        CodeText = codeText;
        CodeLanguage = codeLanguage;
    }

    public MarkdownBlockKind Kind { get; }

    /// <summary>Inlines for <see cref="MarkdownBlockKind.Paragraph"/> / <see cref="MarkdownBlockKind.Heading"/>.</summary>
    public IReadOnlyList<MarkdownInline> Inlines { get; }

    /// <summary>Items for <see cref="MarkdownBlockKind.BulletList"/> / <see cref="MarkdownBlockKind.NumberedList"/>.</summary>
    public IReadOnlyList<MarkdownListItem> Items { get; }

    /// <summary>1–6 for <see cref="MarkdownBlockKind.Heading"/>; 0 otherwise.</summary>
    public int HeadingLevel { get; }

    /// <summary>Raw code for <see cref="MarkdownBlockKind.CodeBlock"/> (newlines preserved, no trailing newline).</summary>
    public string CodeText { get; }

    /// <summary>The fence's info string (e.g. <c>"csharp"</c>); empty if none.</summary>
    public string CodeLanguage { get; }
}

/// <summary>A parsed markdown document — an ordered list of blocks.</summary>
public sealed class MarkdownDocument
{
    public MarkdownDocument(IReadOnlyList<MarkdownBlock> blocks)
    {
        Blocks = blocks;
    }

    public IReadOnlyList<MarkdownBlock> Blocks { get; }
}
