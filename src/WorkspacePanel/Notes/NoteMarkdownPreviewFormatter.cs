using System.Text.RegularExpressions;

namespace WinWidgetBoard.WorkspacePanel.Notes;

public enum NoteMarkdownPreviewBlockKind
{
    Blank,
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    UnorderedListItem,
    OrderedListItem,
    Quote,
    Code,
}

public sealed record NoteMarkdownPreviewBlock(
    NoteMarkdownPreviewBlockKind Kind,
    string Text,
    string Marker = "");

/// <summary>
/// Renders the deliberately small Markdown subset used by the first note preview.
/// This is a text/block formatter, not a general-purpose Markdown parser.
/// </summary>
public static partial class NoteMarkdownPreviewFormatter
{
    private static readonly Regex LinkPattern = CreateLinkPattern();
    private static readonly Regex CodeSpanPattern = CreateCodeSpanPattern();
    private static readonly Regex StrongPattern = CreateStrongPattern();
    private static readonly Regex EmphasisPattern = CreateEmphasisPattern();
    private static readonly Regex EscapePattern = CreateEscapePattern();

    public static IReadOnlyList<NoteMarkdownPreviewBlock> Format(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<NoteMarkdownPreviewBlock>();
        }

        string normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var blocks = new List<NoteMarkdownPreviewBlock>(lines.Length);
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (IsFence(trimmed))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                blocks.Add(new NoteMarkdownPreviewBlock(
                    NoteMarkdownPreviewBlockKind.Code,
                    line.TrimEnd()));
                continue;
            }

            blocks.Add(ParseBlock(line, trimmed));
        }

        while (blocks.Count > 0 && blocks[^1].Kind == NoteMarkdownPreviewBlockKind.Blank)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    private static NoteMarkdownPreviewBlock ParseBlock(string line, string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return new NoteMarkdownPreviewBlock(NoteMarkdownPreviewBlockKind.Blank, string.Empty);
        }

        int headingLevel = CountHeadingLevel(trimmed);
        if (headingLevel > 0)
        {
            string headingText = RenderInline(trimmed[(headingLevel + 1)..].TrimStart());
            return new NoteMarkdownPreviewBlock(
                headingLevel switch
                {
                    1 => NoteMarkdownPreviewBlockKind.Heading1,
                    2 => NoteMarkdownPreviewBlockKind.Heading2,
                    _ => NoteMarkdownPreviewBlockKind.Heading3,
                },
                headingText);
        }

        if (trimmed.Length >= 2 &&
            (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') &&
            char.IsWhiteSpace(trimmed[1]))
        {
            return new NoteMarkdownPreviewBlock(
                NoteMarkdownPreviewBlockKind.UnorderedListItem,
                RenderInline(trimmed[2..].TrimStart()),
                "•");
        }

        int orderedMarkerEnd = FindOrderedMarkerEnd(trimmed);
        if (orderedMarkerEnd > 0)
        {
            return new NoteMarkdownPreviewBlock(
                NoteMarkdownPreviewBlockKind.OrderedListItem,
                RenderInline(trimmed[(orderedMarkerEnd + 1)..].TrimStart()),
                trimmed[..(orderedMarkerEnd + 1)]);
        }

        if (trimmed[0] == '>' &&
            (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1])))
        {
            return new NoteMarkdownPreviewBlock(
                NoteMarkdownPreviewBlockKind.Quote,
                RenderInline(trimmed[1..].TrimStart()),
                "│");
        }

        return new NoteMarkdownPreviewBlock(
            NoteMarkdownPreviewBlockKind.Paragraph,
            RenderInline(line));
    }

    private static int CountHeadingLevel(string value)
    {
        int count = 0;
        while (count < value.Length && value[count] == '#')
        {
            count++;
        }

        return count is >= 1 and <= 3 &&
            count < value.Length &&
            char.IsWhiteSpace(value[count])
            ? count
            : 0;
    }

    private static int FindOrderedMarkerEnd(string value)
    {
        int index = 0;
        while (index < value.Length && char.IsDigit(value[index]))
        {
            index++;
        }

        return index > 0 &&
            index + 1 < value.Length &&
            value[index] == '.' &&
            char.IsWhiteSpace(value[index + 1])
            ? index
            : -1;
    }

    private static bool IsFence(string value) =>
        value.StartsWith("\x60\x60\x60", StringComparison.Ordinal) ||
        value.StartsWith("~~~", StringComparison.Ordinal);

    private static string RenderInline(string value)
    {
        string rendered = LinkPattern.Replace(value, "$1");
        rendered = CodeSpanPattern.Replace(rendered, "$1");
        rendered = StrongPattern.Replace(rendered, "$1");
        rendered = EmphasisPattern.Replace(
            rendered,
            match => match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Value);
        return EscapePattern.Replace(rendered, "$1");
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateLinkPattern();

    [GeneratedRegex("[\x60]([^\x60]+)[\x60]", RegexOptions.CultureInvariant)]
    private static partial Regex CreateCodeSpanPattern();

    [GeneratedRegex(@"(?:\*\*|__)(.+?)(?:\*\*|__)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateStrongPattern();

    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)|(?<!_)_([^_\r\n]+)_(?!_)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateEmphasisPattern();

    [GeneratedRegex(@"\\([\\\x60*_{}\[\]()#+.!>\-])", RegexOptions.CultureInvariant)]
    private static partial Regex CreateEscapePattern();
}
