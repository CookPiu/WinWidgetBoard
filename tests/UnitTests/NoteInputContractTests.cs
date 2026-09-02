using System.Xml.Linq;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteInputContractTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod(DisplayName = "UT-NOTE-049 [NTE-001] Note input boxes cap their length at the IPC contract")]
    public void NoteInputBoxesCapTheirLengthAtTheContract()
    {
        // A box that accepts more than the broker does hands the user a draft whose every
        // retry fails validation, with nothing on screen to say why.
        XDocument mainWindow = LoadAsset("MainWindow.xaml");
        XDocument noteWindow = LoadAsset("NoteWindow.xaml");

        AssertMaxLength(mainWindow, "NoteTitleBox", NotesContract.MaxTitleLength);
        AssertMaxLength(mainWindow, "NoteBodyBox", NotesContract.MaxBodyLength);
        AssertMaxLength(mainWindow, "SearchBox", NotesContract.MaxSearchLength);
        AssertMaxLength(noteWindow, "NoteWindowTitleBox", NotesContract.MaxTitleLength);
        AssertMaxLength(noteWindow, "NoteWindowBodyBox", NotesContract.MaxBodyLength);
    }

    [TestMethod(DisplayName = "UT-NOTE-050 [NTE-001] Caret lands at the end of the region undo or redo changed")]
    public void CaretLandsAtTheEndOfTheChangedRegion()
    {
        // Undoing typed text: the caret goes to where the text was.
        Assert.AreEqual(5, NoteCaretPlacement.AfterChange("hello world", "hello"));
        // Redoing it: the caret follows to the end of what came back.
        Assert.AreEqual(11, NoteCaretPlacement.AfterChange("hello", "hello world"));
        // A change in the middle: just after the inserted run.
        Assert.AreEqual(3, NoteCaretPlacement.AfterChange("abc", "abXc"));
        // A deletion in the middle: where the deleted run was.
        Assert.AreEqual(2, NoteCaretPlacement.AfterChange("abXc", "abc"));
        // Nothing in common: the end of the new text.
        Assert.AreEqual(3, NoteCaretPlacement.AfterChange("one", "two"));
        Assert.AreEqual(0, NoteCaretPlacement.AfterChange("gone", string.Empty));
    }

    [TestMethod(DisplayName = "UT-NOTE-051 [NTE-001/CRD-004] A one-row card browses notes in place of the editor")]
    public async Task AOneRowCardBrowsesNotesInPlaceOfTheEditor()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(BuiltInCardCatalog.NotesInstanceId, CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var item = new CardSurfaceItem(
            Place(CardSize.L),
            noteEditor,
            new NoteSearchViewModel(null),
            editMode,
            runtimeResourceResolver: static key => key);

        // Two rows: the switcher opens above the editor and the list gets a few results.
        Assert.IsFalse(item.IsNoteBrowsingExclusive);
        Assert.AreEqual(180, item.NoteSwitcherListMaxHeight);

        var announced = new List<string>();
        item.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);
        foreach (CardSize size in new[] { CardSize.S, CardSize.M, CardSize.W })
        {
            announced.Clear();
            item.UpdatePlacement(Place(size));
            // One row: about 106 DIP of content, which the search box and this much list
            // fill - so the editor steps aside while the switcher is open.
            Assert.IsTrue(item.IsNoteBrowsingExclusive, $"{size} should browse exclusively.");
            Assert.AreEqual(64, item.NoteSwitcherListMaxHeight, $"{size} list height.");
            CollectionAssert.Contains(announced, nameof(CardSurfaceItem.IsNoteBrowsingExclusive));
            CollectionAssert.Contains(announced, nameof(CardSurfaceItem.NoteSwitcherListMaxHeight));
            item.UpdatePlacement(Place(CardSize.L));
        }
    }

    private static CardPlacement Place(CardSize size)
    {
        CardSpan span = ResponsiveGridLayout.GetSpan(size);
        return new CardPlacement(
            BuiltInCardCatalog.NotesInstanceId,
            size,
            0,
            0,
            span.Columns,
            span.Rows);
    }

    private static void AssertMaxLength(XDocument document, string name, int expected)
    {
        XElement box = document
            .Descendants()
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    name,
                    StringComparison.Ordinal));
        Assert.AreEqual(
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)box.Attribute("MaxLength"),
            $"{name} MaxLength must match the contract.");
    }

    private static XDocument LoadAsset(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName));
}
