using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardLayoutViewModelTests
{
    private static readonly string[] ExpectedCardOrder = ["a", "b"];

    [TestMethod(DisplayName = "UT-GRID-007 [LYT-005] Replacing cards publishes a coherent snapshot")]
    public void ReplacingCardsPublishesCoherentSnapshot()
    {
        var viewModel = new CardLayoutViewModel(4);
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.ReplaceItems(
        [
            new CardLayoutItem("a", CardSize.M),
            new CardLayoutItem("b", CardSize.S),
        ]);

        Assert.AreEqual(2, viewModel.Items.Count);
        Assert.AreEqual(2, viewModel.Placements.Count);
        Assert.IsFalse(viewModel.IsEmpty);
        CollectionAssert.Contains(changed, nameof(CardLayoutViewModel.Items));
        CollectionAssert.Contains(changed, nameof(CardLayoutViewModel.Placements));
        Assert.IsTrue(viewModel.TryGetPlacement("b", out CardPlacement placement));
        Assert.AreEqual(2, placement.Column);
    }

    [TestMethod(DisplayName = "UT-GRID-008 [LYT-001] Changing width repacks with the same card order")]
    public void ChangingWidthRepacksWithSameCardOrder()
    {
        var viewModel = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("a", CardSize.W),
                new CardLayoutItem("b", CardSize.M),
            ]);

        viewModel.SetEffectiveWidth(500);

        Assert.AreEqual(2, viewModel.ColumnCount);
        CollectionAssert.AreEqual(
            ExpectedCardOrder,
            viewModel.Items.Select(item => item.InstanceId).ToArray());
        Assert.AreEqual(2, viewModel.Placements[0].ColumnSpan);
        Assert.AreEqual(2, viewModel.Placements[1].ColumnSpan);
    }

    [TestMethod(DisplayName = "UT-GRID-009 [LYT-005] Add, resize and remove use deterministic repacking")]
    public void AddResizeAndRemoveUseDeterministicRepacking()
    {
        var viewModel = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("a", CardSize.S)]);

        viewModel.AddCard(new CardLayoutItem("b", CardSize.S));
        Assert.IsTrue(viewModel.ResizeCard("b", CardSize.M));
        Assert.IsTrue(viewModel.RemoveCard("a"));

        Assert.AreEqual(1, viewModel.Items.Count);
        Assert.AreEqual("b", viewModel.Items[0].InstanceId);
        Assert.AreEqual(2, viewModel.Placements[0].ColumnSpan);
        Assert.AreEqual(0, viewModel.Placements[0].Column);
        Assert.IsFalse(viewModel.RemoveCard("missing"));
    }

    [TestMethod(DisplayName = "UT-GRID-010 [LYT-005] Invalid replacement leaves the previous snapshot intact")]
    public void InvalidReplacementLeavesPreviousSnapshotIntact()
    {
        var viewModel = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("stable", CardSize.L)]);

        Assert.Throws<ArgumentException>(() => viewModel.ReplaceItems(
        [
            new CardLayoutItem("duplicate", CardSize.S),
            new CardLayoutItem("duplicate", CardSize.M),
        ]));

        Assert.AreEqual(1, viewModel.Items.Count);
        Assert.AreEqual("stable", viewModel.Items[0].InstanceId);
        Assert.AreEqual(CardSize.L, viewModel.Items[0].Size);
    }

    [TestMethod(DisplayName = "UT-GRID-011 [LYT-005] Unknown card lookup is side-effect free")]
    public void UnknownCardLookupIsSideEffectFree()
    {
        var viewModel = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("stable", CardSize.S)]);

        Assert.IsFalse(viewModel.ResizeCard("missing", CardSize.L));
        Assert.IsFalse(viewModel.TryGetPlacement("missing", out _));
        Assert.AreEqual(1, viewModel.Items.Count);
        Assert.AreEqual(1, viewModel.Placements.Count);
    }

    [TestMethod(DisplayName = "UT-GRID-024 [LYT-006] Committed placement updates stable persistence order")]
    public void CommittedPlacementUpdatesStablePersistenceOrder()
    {
        var viewModel = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("dragged", CardSize.S),
            ]);

        viewModel.ApplyPlacements(
        [
            new CardPlacement("first", CardSize.M, 0, 1, 2, 1),
            new CardPlacement("dragged", CardSize.S, 2, 0, 1, 1),
        ]);

        string[] expectedStableOrder = ["first", "dragged"];
        string[] expectedVisualOrder = ["dragged", "first"];
        CollectionAssert.AreEqual(
            expectedStableOrder,
            viewModel.Items.Select(item => item.InstanceId).ToArray());
        CollectionAssert.AreEqual(
            expectedStableOrder,
            viewModel.Placements.Select(placement => placement.InstanceId).ToArray());
        CollectionAssert.AreEqual(
            expectedVisualOrder,
            viewModel.GetItemsInPlacementOrder()
                .Select(item => item.InstanceId)
                .ToArray());
        Assert.AreEqual(0, viewModel.Items[0].PreferredColumn);
        Assert.AreEqual(2, viewModel.Items[1].PreferredColumn);
        Assert.AreEqual(1, viewModel.Items[0].PreferredRow);
        Assert.AreEqual(0, viewModel.Items[1].PreferredRow);
    }
}
