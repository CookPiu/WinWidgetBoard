using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteMarkdownPreviewFormatterTests
{
    [TestMethod(DisplayName = "UT-NOTE-023 [NTE-001] Markdown preview renders basic blocks")]
    public void MarkdownPreviewRendersBasicBlocks()
    {
        IReadOnlyList<NoteMarkdownPreviewBlock> blocks =
            NoteMarkdownPreviewFormatter.Format(
                "# Heading\r\n\r\n- **bold** item\r\n1. [linked](https://example.test)\r\n> quote");

        Assert.AreEqual(5, blocks.Count);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Heading1, blocks[0].Kind);
        Assert.AreEqual("Heading", blocks[0].Text);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Blank, blocks[1].Kind);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.UnorderedListItem, blocks[2].Kind);
        Assert.AreEqual("bold item", blocks[2].Text);
        Assert.AreEqual("•", blocks[2].Marker);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.OrderedListItem, blocks[3].Kind);
        Assert.AreEqual("linked", blocks[3].Text);
        Assert.AreEqual("1.", blocks[3].Marker);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Quote, blocks[4].Kind);
        Assert.AreEqual("quote", blocks[4].Text);
    }

    [TestMethod(DisplayName = "UT-NOTE-024 [NTE-001] Markdown preview renders fenced code and inline syntax")]
    public void MarkdownPreviewRendersFencedCodeAndInlineSyntax()
    {
        IReadOnlyList<NoteMarkdownPreviewBlock> blocks =
            NoteMarkdownPreviewFormatter.Format(
                "Use \u0060code\u0060 and _emphasis_.\n\u0060\u0060\u0060csharp\nvar answer = 42;\n\u0060\u0060\u0060\n");

        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Paragraph, blocks[0].Kind);
        Assert.AreEqual("Use code and emphasis.", blocks[0].Text);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Code, blocks[1].Kind);
        Assert.AreEqual("var answer = 42;", blocks[1].Text);
    }
}
