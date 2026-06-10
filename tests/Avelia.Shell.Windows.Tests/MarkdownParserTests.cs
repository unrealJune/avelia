using System.Linq;
using Avelia.Shell.Windows.Controls.Markdown;
using Xunit;

namespace Avelia.Shell.Windows.Tests;

public class MarkdownParserTests
{
    [Fact]
    public void Empty_source_yields_no_blocks()
    {
        Assert.Empty(MarkdownParser.Parse("").Blocks);
        Assert.Empty(MarkdownParser.Parse(null).Blocks);
        Assert.Empty(MarkdownParser.Parse("   \n  \n").Blocks);
    }

    [Fact]
    public void Plain_paragraph_is_a_single_text_inline()
    {
        var doc = MarkdownParser.Parse("hello world");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        var inline = Assert.Single(block.Inlines);
        Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
        Assert.Equal("hello world", inline.Text);
    }

    [Fact]
    public void Consecutive_lines_join_into_one_paragraph_blank_line_splits()
    {
        var doc = MarkdownParser.Parse("line one\nline two\n\nsecond para");

        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("line one line two", doc.Blocks[0].Inlines.Single().Text);
        Assert.Equal("second para", doc.Blocks[1].Inlines.Single().Text);
    }

    [Theory]
    [InlineData("# Title", 1, "Title")]
    [InlineData("### Deep", 3, "Deep")]
    [InlineData("###### Six", 6, "Six")]
    public void Atx_headings_capture_level_and_text(string src, int level, string text)
    {
        var block = Assert.Single(MarkdownParser.Parse(src).Blocks);

        Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
        Assert.Equal(level, block.HeadingLevel);
        Assert.Equal(text, block.Inlines.Single().Text);
    }

    [Fact]
    public void Fenced_code_block_preserves_content_and_language()
    {
        var doc = MarkdownParser.Parse("```csharp\nvar x = 1;\nvar y = 2;\n```");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("csharp", block.CodeLanguage);
        Assert.Equal("var x = 1;\nvar y = 2;", block.CodeText);
    }

    [Fact]
    public void Code_block_content_is_not_interpreted_as_markdown()
    {
        var doc = MarkdownParser.Parse("```\n# not a heading\n- not a list\n```");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("# not a heading\n- not a list", block.CodeText);
    }

    [Fact]
    public void Unterminated_fence_still_produces_a_code_block()
    {
        var doc = MarkdownParser.Parse("```\nunclosed");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("unclosed", block.CodeText);
    }

    [Fact]
    public void Bullet_list_collects_consecutive_items()
    {
        var doc = MarkdownParser.Parse("- one\n- two\n* three");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.BulletList, block.Kind);
        Assert.Equal(3, block.Items.Count);
        Assert.Equal("one", block.Items[0].Inlines.Single().Text);
        Assert.Equal("three", block.Items[2].Inlines.Single().Text);
    }

    [Fact]
    public void Numbered_list_captures_ordinals()
    {
        var doc = MarkdownParser.Parse("1. first\n2. second\n3. third");

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(MarkdownBlockKind.NumberedList, block.Kind);
        Assert.Equal(3, block.Items.Count);
        Assert.Equal(1, block.Items[0].Number);
        Assert.Equal(3, block.Items[2].Number);
        Assert.Equal("second", block.Items[1].Inlines.Single().Text);
    }

    [Fact]
    public void Bold_and_italic_inlines_are_recognised()
    {
        var inlines = MarkdownParser.ParseInlines("a **bold** and *italic* end");

        Assert.Collection(
            inlines,
            i => Assert.Equal((MarkdownInlineKind.Text, "a "), (i.Kind, i.Text)),
            i => Assert.Equal((MarkdownInlineKind.Bold, "bold"), (i.Kind, i.Text)),
            i => Assert.Equal((MarkdownInlineKind.Text, " and "), (i.Kind, i.Text)),
            i => Assert.Equal((MarkdownInlineKind.Italic, "italic"), (i.Kind, i.Text)),
            i => Assert.Equal((MarkdownInlineKind.Text, " end"), (i.Kind, i.Text))
        );
    }

    [Fact]
    public void Underscore_delimiters_are_supported()
    {
        var inlines = MarkdownParser.ParseInlines("__b__ _i_");

        Assert.Equal(MarkdownInlineKind.Bold, inlines[0].Kind);
        Assert.Equal("b", inlines[0].Text);
        Assert.Equal(MarkdownInlineKind.Italic, inlines[2].Kind);
        Assert.Equal("i", inlines[2].Text);
    }

    [Fact]
    public void Inline_code_content_is_literal()
    {
        var inlines = MarkdownParser.ParseInlines("call `git push --force` now");

        Assert.Equal(MarkdownInlineKind.Code, inlines[1].Kind);
        Assert.Equal("git push --force", inlines[1].Text);
    }

    [Fact]
    public void Links_capture_text_and_url()
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("[GitHub](https://github.com)"));

        Assert.Equal(MarkdownInlineKind.Link, inline.Kind);
        Assert.Equal("GitHub", inline.Text);
        Assert.Equal("https://github.com", inline.Url);
    }

    [Fact]
    public void Code_refs_are_highlighted_inline()
    {
        var inlines = MarkdownParser.ParseInlines("edited @Program.cs today");

        Assert.Equal(MarkdownInlineKind.CodeRef, inlines[1].Kind);
        Assert.Equal("@Program.cs", inlines[1].Text);
    }

    [Fact]
    public void Unclosed_emphasis_is_treated_as_literal_text()
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("a * lonely asterisk"));

        Assert.Equal(MarkdownInlineKind.Text, inline.Kind);
        Assert.Equal("a * lonely asterisk", inline.Text);
    }

    [Fact]
    public void Mixed_document_parses_all_block_kinds_in_order()
    {
        var src =
            "# Heading\n\nA paragraph with `code`.\n\n- bullet\n\n```\nfenced\n```\n\n1. step";

        var kinds = MarkdownParser.Parse(src).Blocks.Select(b => b.Kind).ToArray();

        Assert.Equal(
            new[]
            {
                MarkdownBlockKind.Heading,
                MarkdownBlockKind.Paragraph,
                MarkdownBlockKind.BulletList,
                MarkdownBlockKind.CodeBlock,
                MarkdownBlockKind.NumberedList,
            },
            kinds
        );
    }
}
