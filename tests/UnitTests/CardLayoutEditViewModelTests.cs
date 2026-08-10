using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardLayoutEditViewModelTests
{
    private static readonly string[] ExpectedCardIds = ["first", "second"];

    [TestMethod(DisplayName = "UT-GRID-014 [LYT-003] Card operations require explicit edit mode")]
    public void CardOperationsRequireExplicitEditMode()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("card", CardSize.M)]);
        var editMode = new CardLayoutEditViewModel(layout);

        Assert.IsFalse(editMode.TryResizeCard("card", CardSize.L));
        Assert.IsFalse(editMode.TryRemoveCard("card"));
        Assert.AreEqual(CardSize.M, layout.Items[0].Size);

        editMode.BeginEdit();

        Assert.IsTrue(editMode.IsEditing);
        Assert.IsTrue(editMode.TryResizeCard("card", CardSize.L));
        Assert.AreEqual(CardSize.L, layout.Items[0].Size);
    }

    [TestMethod(DisplayName = "UT-GRID-015 [LYT-003] Cancel edit restores the entry snapshot")]
    public void CancelEditRestoresEntrySnapshot()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.S),
                new CardLayoutItem("second", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        var changed = new List<string>();
        editMode.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        editMode.BeginEdit();
        Assert.IsTrue(editMode.TryRemoveCard("first"));
        Assert.IsTrue(editMode.TryResizeCard("second", CardSize.L));

        editMode.CancelEdit();

        Assert.IsFalse(editMode.IsEditing);
        CollectionAssert.AreEqual(
            ExpectedCardIds,
            layout.Items.Select(item => item.InstanceId).ToArray());
        Assert.AreEqual(CardSize.S, layout.Items[0].Size);
        Assert.AreEqual(CardSize.M, layout.Items[1].Size);
        CollectionAssert.Contains(changed, nameof(CardLayoutEditViewModel.IsEditing));
    }

    [TestMethod(DisplayName = "UT-GRID-016 [LYT-003] Commit edit keeps the working layout")]
    public void CommitEditKeepsWorkingLayout()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("card", CardSize.M)]);
        var editMode = new CardLayoutEditViewModel(layout);

        editMode.BeginEdit();
        Assert.IsTrue(editMode.TryResizeCard("card", CardSize.XL));
        editMode.CommitEdit();

        Assert.IsFalse(editMode.IsEditing);
        Assert.AreEqual(CardSize.XL, layout.Items[0].Size);
        Assert.IsFalse(editMode.TryRemoveCard("card"));
    }

    [TestMethod(DisplayName = "UT-GRID-020 [LYT-004] Commit drop applies the projected placement")]
    public void CommitDropAppliesProjectedPlacement()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("dragged", CardSize.S),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryCommitDrop(
            "dragged",
            new GridCell(3, 0),
            out IReadOnlyList<CardPlacement> committed));

        CardPlacement dragged = committed.Single(
            placement => placement.InstanceId == "dragged");
        Assert.AreEqual(3, dragged.Column);
        Assert.AreEqual(0, dragged.Row);
        Assert.IsTrue(layout.TryGetPlacement("dragged", out CardPlacement actual));
        Assert.AreEqual(dragged, actual);
        Assert.AreEqual(3, layout.Items.Single(item => item.InstanceId == "dragged").PreferredColumn);
    }

    [TestMethod(DisplayName = "UT-GRID-021 [LYT-004] Cancel after a drop restores the committed edit snapshot")]
    public void CancelAfterDropRestoresCommittedEditSnapshot()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("dragged", CardSize.S),
            ]);
        CardPlacement original = layout.Placements.Single(
            placement => placement.InstanceId == "dragged");
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();
        Assert.IsTrue(editMode.TryCommitDrop(
            "dragged",
            new GridCell(3, 0),
            out _));

        editMode.CancelEdit();

        Assert.IsTrue(layout.TryGetPlacement("dragged", out CardPlacement restored));
        Assert.AreEqual(original, restored);
        Assert.IsFalse(editMode.IsEditing);
    }
}
