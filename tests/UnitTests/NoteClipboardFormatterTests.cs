using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteClipboardFormatterTests
{
    [TestMethod(DisplayName = "UT-NOTE-010 [NTE-001] Copy format preserves title and Markdown body")]
    public void CopyFormatPreservesTitleAndBody()
    {
        Assert.AreEqual(
            string.Concat("Title", Environment.NewLine, "**body**"),
            NoteClipboardFormatter.Format("Title", "**body**"));
    }

    [TestMethod(DisplayName = "UT-NOTE-011 [NTE-001] Copy format omits empty sections")]
    public void CopyFormatOmitsEmptySections()
    {
        Assert.AreEqual(
            "body",
            NoteClipboardFormatter.Format(string.Empty, "body"));
        Assert.AreEqual(
            "title",
            NoteClipboardFormatter.Format("title", string.Empty));
        Assert.AreEqual(
            string.Empty,
            NoteClipboardFormatter.Format(null, null));
    }
}
