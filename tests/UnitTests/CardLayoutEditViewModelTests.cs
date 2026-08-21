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

    [TestMethod(DisplayName = "UT-GRID-046 [LYT-003] Sequential size steps use each target card current size")]
    public void SequentialSizeStepsUseEachTargetCardCurrentSize()
    {
        CardSize[] orderedSizes =
        [
            CardSize.S,
            CardSize.M,
            CardSize.L,
            CardSize.W,
            CardSize.XL,
        ];
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.L),
                new CardLayoutItem("second", CardSize.S),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryStepCardSize(
            "first",
            1,
            orderedSizes));
        Assert.IsTrue(editMode.TryStepCardSize(
            "second",
            1,
            orderedSizes));

        Assert.AreEqual(
            CardSize.W,
            layout.Items.Single(item => item.InstanceId == "first").Size);
        Assert.AreEqual(
            CardSize.M,
            layout.Items.Single(item => item.InstanceId == "second").Size);

        Assert.IsTrue(editMode.TryUndo());
        Assert.AreEqual(
            CardSize.S,
            layout.Items.Single(item => item.InstanceId == "second").Size);
        Assert.AreEqual(
            CardSize.W,
            layout.Items.Single(item => item.InstanceId == "first").Size);
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
        Assert.IsFalse(editMode.CanUndo);
        Assert.IsFalse(editMode.CanRedo);
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
        Assert.IsFalse(editMode.CanUndo);
        Assert.IsFalse(editMode.CanRedo);
        Assert.AreEqual(CardSize.XL, layout.Items[0].Size);
        Assert.IsFalse(editMode.TryRemoveCard("card"));
    }

    [TestMethod(DisplayName = "UT-GRID-035 [LYT-006] Undo and redo restore resize and move commands")]
    public void UndoAndRedoRestoreResizeAndMoveCommands()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("dragged", CardSize.S),
            ]);
        CardPlacement originalDragged = layout.Placements.Single(
            placement => placement.InstanceId == "dragged");
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryResizeCard("first", CardSize.L));
        Assert.IsTrue(editMode.TryCommitDrop(
            "dragged",
            new GridCell(3, 0),
            out _));
        Assert.IsTrue(editMode.CanUndo);
        Assert.IsFalse(editMode.CanRedo);

        Assert.IsTrue(editMode.TryUndo());
        Assert.AreEqual(CardSize.L, layout.Items.Single(
            item => item.InstanceId == "first").Size);
        Assert.AreEqual(
            originalDragged,
            layout.Placements.Single(placement => placement.InstanceId == "dragged"));

        Assert.IsTrue(editMode.TryUndo());
        Assert.AreEqual(CardSize.M, layout.Items.Single(
            item => item.InstanceId == "first").Size);
        Assert.AreEqual(
            originalDragged,
            layout.Placements.Single(placement => placement.InstanceId == "dragged"));
        Assert.IsFalse(editMode.CanUndo);
        Assert.IsTrue(editMode.CanRedo);

        Assert.IsTrue(editMode.TryRedo());
        Assert.AreEqual(CardSize.L, layout.Items.Single(
            item => item.InstanceId == "first").Size);
        Assert.IsTrue(editMode.TryRedo());
        Assert.AreEqual(
            3,
            layout.Placements.Single(placement => placement.InstanceId == "dragged").Column);
        Assert.IsFalse(editMode.CanRedo);
    }

    [TestMethod(DisplayName = "UT-GRID-036 [LYT-006] New edit command clears redo history")]
    public void NewEditCommandClearsRedoHistory()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("card", CardSize.S)]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryResizeCard("card", CardSize.M));
        Assert.IsTrue(editMode.TryUndo());
        Assert.IsTrue(editMode.CanRedo);

        Assert.IsTrue(editMode.TryResizeCard("card", CardSize.L));
        Assert.IsFalse(editMode.CanRedo);
        Assert.IsTrue(editMode.TryUndo());
        Assert.AreEqual(CardSize.S, layout.Items[0].Size);
    }

    [TestMethod(DisplayName = "UT-GRID-037 [LYT-006] Layout history is bounded to twenty undo steps")]
    public void LayoutHistoryIsBoundedToTwentyUndoSteps()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("card", CardSize.S)]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        for (int index = 0; index < CardLayoutEditViewModel.MaxHistoryDepth + 1; index++)
        {
            CardSize nextSize = index % 2 == 0 ? CardSize.M : CardSize.S;
            Assert.IsTrue(editMode.TryResizeCard("card", nextSize));
        }

        int undoCount = 0;
        while (editMode.TryUndo())
        {
            undoCount++;
        }

        Assert.AreEqual(CardLayoutEditViewModel.MaxHistoryDepth, undoCount);
        Assert.IsFalse(editMode.CanUndo);
        Assert.AreEqual(CardSize.M, layout.Items[0].Size);
    }

    [TestMethod(DisplayName = "UT-GRID-038 [LYT-006] Undo restores a removed card")]
    public void UndoRestoresRemovedCard()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("second", CardSize.S),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryRemoveCard("first"));
        Assert.IsFalse(layout.Items.Any(item => item.InstanceId == "first"));

        Assert.IsTrue(editMode.TryUndo());
        Assert.IsTrue(layout.Items.Any(item => item.InstanceId == "first"));
        Assert.AreEqual(2, layout.Items.Count);
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

    [TestMethod(DisplayName = "UT-GRID-050 [LYT-007] Adding a card requires edit mode and appends once")]
    public void AddingCardRequiresEditModeAndAppendsOnce()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("first", CardSize.M)]);
        var editMode = new CardLayoutEditViewModel(layout);

        Assert.IsFalse(editMode.TryAddCard("second", CardSize.M));
        Assert.AreEqual(1, layout.Items.Count);

        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryAddCard("second", CardSize.L));
        Assert.AreEqual(
            CardSize.L,
            layout.Items.Single(item => item.InstanceId == "second").Size);

        // Built-in instances are singletons; a second add of the same identity is refused
        // rather than producing two cards that share a broker subscription.
        Assert.IsFalse(editMode.TryAddCard("second", CardSize.M));
        Assert.AreEqual(2, layout.Items.Count);
    }

    [TestMethod(DisplayName = "UT-GRID-051 [LYT-007] Undo removes an added card and redo brings it back")]
    public void UndoRemovesAddedCardAndRedoBringsItBack()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("first", CardSize.M)]);
        var editMode = new CardLayoutEditViewModel(layout);
        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryAddCard("second", CardSize.M));
        Assert.IsTrue(editMode.CanUndo);

        Assert.IsTrue(editMode.TryUndo());
        Assert.AreEqual(1, layout.Items.Count);

        Assert.IsTrue(editMode.TryRedo());
        CollectionAssert.AreEquivalent(
            ExpectedCardIds,
            layout.Items.Select(item => item.InstanceId).ToArray());

        // Cancelling the session drops the addition with everything else in it.
        editMode.CancelEdit();
        Assert.AreEqual(1, layout.Items.Count);
    }
}
