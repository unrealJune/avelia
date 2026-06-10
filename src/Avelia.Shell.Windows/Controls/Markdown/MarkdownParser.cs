using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Avelia.Shell.Windows.Controls.Markdown;

/// <summary>
/// A deliberately small, dependency-free markdown parser covering the subset
/// agents actually emit: ATX headings, fenced code blocks, bullet / numbered
/// lists, paragraphs, and inline <c>**bold**</c>, <c>*italic*</c>,
/// <c>`code`</c>, <c>[links](url)</c>, plus Avelia's own <c>@file.ext</c>
/// code-refs. It is pure logic with no WinUI dependency so it can be unit-tested
/// in the platform-independent shell-tests assembly.
///
/// It is intentionally forgiving: anything it doesn't recognise falls through as
/// plain text rather than throwing, so a malformed agent reply never breaks the
/// transcript.
/// </summary>
public static class MarkdownParser
{
    // @file.ext code-ref — same shape CodeRefBlock used before markdown landed.
    private static readonly Regex CodeRefRegex = new(
        @"(?<=^|\s)@([A-Za-z0-9_\-.]+\.[A-Za-z0-9]+)",
        RegexOptions.Compiled
    );

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new(
        @"^[ \t]*[-*+][ \t]+(.*)$",
        RegexOptions.Compiled
    );

    private static readonly Regex NumberedRegex = new(
        @"^[ \t]*(\d+)[.)][ \t]+(.*)$",
        RegexOptions.Compiled
    );

    private static readonly Regex FenceRegex = new(
        @"^[ \t]*(`{3,}|~{3,})[ \t]*([^`~]*)$",
        RegexOptions.Compiled
    );

    private static readonly Regex LinkRegex = new(
        @"^\[([^\]]*)\]\(([^)\s]+)\)",
        RegexOptions.Compiled
    );

    /// <summary>Parse <paramref name="source"/> into a block list.</summary>
    public static MarkdownDocument Parse(string? source)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(source))
        {
            return new MarkdownDocument(blocks);
        }

        // Normalise newlines so the line splitter behaves the same on CRLF / CR.
        var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Blank lines separate blocks.
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            // Fenced code block.
            var fence = FenceRegex.Match(line);
            if (fence.Success)
            {
                var fenceToken = fence.Groups[1].Value;
                var language = fence.Groups[2].Value.Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !IsClosingFence(lines[i], fenceToken))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                // Skip the closing fence if present.
                if (i < lines.Length)
                {
                    i++;
                }
                blocks.Add(
                    new MarkdownBlock(
                        MarkdownBlockKind.CodeBlock,
                        codeText: TrimTrailingNewline(code.ToString()),
                        codeLanguage: language
                    )
                );
                continue;
            }

            // Heading.
            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var text = heading.Groups[2].Value.Trim();
                blocks.Add(
                    new MarkdownBlock(
                        MarkdownBlockKind.Heading,
                        inlines: ParseInlines(text),
                        headingLevel: level
                    )
                );
                i++;
                continue;
            }

            // Bullet list — consume consecutive bullet lines.
            if (BulletRegex.IsMatch(line))
            {
                var items = new List<MarkdownListItem>();
                while (i < lines.Length && BulletRegex.Match(lines[i]) is { Success: true } m)
                {
                    items.Add(
                        new MarkdownListItem(ParseInlines(m.Groups[1].Value), items.Count + 1)
                    );
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.BulletList, items: items));
                continue;
            }

            // Numbered list — consume consecutive numbered lines.
            if (NumberedRegex.IsMatch(line))
            {
                var items = new List<MarkdownListItem>();
                while (i < lines.Length && NumberedRegex.Match(lines[i]) is { Success: true } m)
                {
                    var number = int.TryParse(m.Groups[1].Value, out var n) ? n : items.Count + 1;
                    items.Add(new MarkdownListItem(ParseInlines(m.Groups[2].Value), number));
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.NumberedList, items: items));
                continue;
            }

            // Paragraph — gather consecutive "plain" lines, soft-joined by space.
            var paragraph = new StringBuilder();
            while (i < lines.Length && IsParagraphLine(lines[i]))
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }
                paragraph.Append(lines[i].Trim());
                i++;
            }
            blocks.Add(
                new MarkdownBlock(
                    MarkdownBlockKind.Paragraph,
                    inlines: ParseInlines(paragraph.ToString())
                )
            );
        }

        return new MarkdownDocument(blocks);
    }

    private static bool IsParagraphLine(string line)
    {
        if (line.Trim().Length == 0)
        {
            return false;
        }
        return !FenceRegex.IsMatch(line)
            && !HeadingRegex.IsMatch(line)
            && !BulletRegex.IsMatch(line)
            && !NumberedRegex.IsMatch(line);
    }

    private static bool IsClosingFence(string line, string openingToken)
    {
        var trimmed = line.Trim();
        var fenceChar = openingToken[0];
        if (trimmed.Length < openingToken.Length)
        {
            return false;
        }
        foreach (var c in trimmed)
        {
            if (c != fenceChar)
            {
                return false;
            }
        }
        return true;
    }

    private static string TrimTrailingNewline(string s) => s.EndsWith('\n') ? s[..^1] : s;

    /// <summary>
    /// Scan a single line of text into styled inlines. Single forward pass;
    /// unrecognised delimiters are emitted as literal text so the scanner can
    /// never stall or throw.
    /// </summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var inlines = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text))
        {
            return inlines;
        }

        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }
            EmitTextWithCodeRefs(plain.ToString(), inlines);
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Inline code — literal content, highest precedence.
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushPlain();
                    inlines.Add(
                        new MarkdownInline(
                            MarkdownInlineKind.Code,
                            text.Substring(i + 1, close - i - 1)
                        )
                    );
                    i = close + 1;
                    continue;
                }
            }
            // Link [text](url).
            else if (c == '[')
            {
                var link = LinkRegex.Match(text[i..]);
                if (link.Success)
                {
                    FlushPlain();
                    inlines.Add(
                        new MarkdownInline(
                            MarkdownInlineKind.Link,
                            link.Groups[1].Value,
                            link.Groups[2].Value
                        )
                    );
                    i += link.Length;
                    continue;
                }
            }
            // Bold / italic with * or _.
            else if (c == '*' || c == '_')
            {
                var isDouble = i + 1 < text.Length && text[i + 1] == c;
                var delimiter = isDouble ? new string(c, 2) : c.ToString();
                var contentStart = i + delimiter.Length;
                var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
                if (close > contentStart)
                {
                    var content = text.Substring(contentStart, close - contentStart);
                    FlushPlain();
                    inlines.Add(
                        new MarkdownInline(
                            isDouble ? MarkdownInlineKind.Bold : MarkdownInlineKind.Italic,
                            content
                        )
                    );
                    i = close + delimiter.Length;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
        return inlines;
    }

    /// <summary>Split a plain run into <c>Text</c> + <c>CodeRef</c> inlines.</summary>
    private static void EmitTextWithCodeRefs(string text, List<MarkdownInline> inlines)
    {
        var idx = 0;
        foreach (Match match in CodeRefRegex.Matches(text))
        {
            if (match.Index > idx)
            {
                inlines.Add(
                    new MarkdownInline(
                        MarkdownInlineKind.Text,
                        text.Substring(idx, match.Index - idx)
                    )
                );
            }
            inlines.Add(new MarkdownInline(MarkdownInlineKind.CodeRef, match.Value));
            idx = match.Index + match.Length;
        }
        if (idx < text.Length)
        {
            inlines.Add(new MarkdownInline(MarkdownInlineKind.Text, text[idx..]));
        }
    }
}
