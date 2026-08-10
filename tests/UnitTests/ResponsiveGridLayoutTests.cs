using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ResponsiveGridLayoutTests
{
    [TestMethod(DisplayName = "UT-GRID-001 [LYT-001] Effective width selects 2, 4 or 6 columns")]
    public void EffectiveWidthSelectsResponsiveColumnCount()
    {
        Assert.AreEqual(2, ResponsiveGridLayout.SelectColumnCount(599));
        Assert.AreEqual(4, ResponsiveGridLayout.SelectColumnCount(600));
        Assert.AreEqual(4, ResponsiveGridLayout.SelectColumnCount(899));
        Assert.AreEqual(6, ResponsiveGridLayout.SelectColumnCount(900));
    }

    [TestMethod(DisplayName = "UT-GRID-002 [LYT-002] Standard card sizes map to declared spans")]
    public void StandardCardSizesMapToSpans()
    {
        Assert.AreEqual(new CardSpan(1, 1), ResponsiveGridLayout.GetSpan(CardSize.S));
        Assert.AreEqual(new CardSpan(2, 1), ResponsiveGridLayout.GetSpan(CardSize.M));
        Assert.AreEqual(new CardSpan(2, 2), ResponsiveGridLayout.GetSpan(CardSize.L));
        Assert.AreEqual(new CardSpan(4, 1), ResponsiveGridLayout.GetSpan(CardSize.W));
        Assert.AreEqual(new CardSpan(4, 2), ResponsiveGridLayout.GetSpan(CardSize.XL));
    }

    [TestMethod(DisplayName = "UT-GRID-003 [LYT-005] Packing is stable top-left first-fit")]
    public void PackingIsStableTopLeftFirstFit()
    {
        CardLayoutItem[] items =
        [
            new("a", CardSize.M),
            new("b", CardSize.S),
            new("c", CardSize.L),
            new("d", CardSize.W),
        ];

        IReadOnlyList<CardPlacement> first = ResponsiveGridLayout.Pack(4, items);
        IReadOnlyList<CardPlacement> second = ResponsiveGridLayout.Pack(4, items);

        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        AssertPlacement(first[0], "a", 0, 0, 2, 1);
        AssertPlacement(first[1], "b", 2, 0, 1, 1);
        AssertPlacement(first[2], "c", 0, 1, 2, 2);
        AssertPlacement(first[3], "d", 0, 3, 4, 1);
    }

    [TestMethod(DisplayName = "UT-GRID-004 [LYT-001] Wide sizes shrink to fit a 2-column grid")]
    public void WideSizesShrinkToFitNarrowGrid()
    {
        IReadOnlyList<CardPlacement> placements = ResponsiveGridLayout.Pack(
            2,
            [
                new CardLayoutItem("wide", CardSize.W),
                new CardLayoutItem("extra-wide", CardSize.XL),
            ]);

        AssertPlacement(placements[0], "wide", 0, 0, 2, 1);
        AssertPlacement(placements[1], "extra-wide", 0, 1, 2, 2);
    }

    [TestMethod(DisplayName = "UT-GRID-006 [LYT-006] Saved logical cells are replayed without compacting gaps")]
    public void SavedLogicalCellsAreReplayedWithoutCompactingGaps()
    {
        CardLayoutItem[] items =
        [
            new("lower", CardSize.M, 0, 3),
            new("upper", CardSize.S, 3, 0),
        ];

        IReadOnlyList<CardPlacement> placements = ResponsiveGridLayout.Pack(4, items);

        AssertPlacement(placements[0], "lower", 0, 3, 2, 1);
        AssertPlacement(placements[1], "upper", 3, 0, 1, 1);
    }

    [TestMethod(DisplayName = "UT-GRID-007 [LYT-005] One hundred random cards stay bounded and do not overlap")]
    public void OneHundredRandomCardsStayBoundedAndDoNotOverlap()
    {
        var random = new Random(20260808);
        CardSize[] sizes = Enum.GetValues<CardSize>();
        var items = new CardLayoutItem[100];
        for (int index = 0; index < items.Length; index++)
        {
            int? preferredColumn = random.Next(0, 8) == 0
                ? random.Next(0, 8)
                : null;
            items[index] = new CardLayoutItem(
                $"card-{index:000}",
                sizes[random.Next(sizes.Length)],
                preferredColumn);
        }

        foreach (int columnCount in new[] { 2, 4, 6 })
        {
            IReadOnlyList<CardPlacement> placements = ResponsiveGridLayout.Pack(
                columnCount,
                items);
            Assert.AreEqual(items.Length, placements.Count);

            for (int index = 0; index < placements.Count; index++)
            {
                CardPlacement placement = placements[index];
                Assert.IsTrue(placement.Column >= 0);
                Assert.IsTrue(placement.Row >= 0);
                Assert.IsTrue(placement.RightExclusive <= columnCount);

                for (int otherIndex = index + 1; otherIndex < placements.Count; otherIndex++)
                {
                    Assert.IsFalse(Overlaps(placement, placements[otherIndex]));
                }
            }
        }
    }

    [TestMethod(DisplayName = "UT-GRID-008 [LYT-005] Removing a card compacts later cards stably")]
    public void RemovingCardCompactsLaterCardsWithoutReordering()
    {
        CardLayoutItem[] allItems =
        [
            new("a", CardSize.S),
            new("b", CardSize.S),
            new("c", CardSize.S),
        ];
        CardLayoutItem[] withoutMiddle =
        [
            allItems[0],
            allItems[2],
        ];

        IReadOnlyList<CardPlacement> all = ResponsiveGridLayout.Pack(4, allItems);
        IReadOnlyList<CardPlacement> compacted = ResponsiveGridLayout.Pack(4, withoutMiddle);

        AssertPlacement(all[0], "a", 0, 0, 1, 1);
        AssertPlacement(all[2], "c", 2, 0, 1, 1);
        AssertPlacement(compacted[0], "a", 0, 0, 1, 1);
        AssertPlacement(compacted[1], "c", 1, 0, 1, 1);
    }

    private static void AssertPlacement(
        CardPlacement placement,
        string instanceId,
        int column,
        int row,
        int columnSpan,
        int rowSpan)
    {
        Assert.AreEqual(instanceId, placement.InstanceId);
        Assert.AreEqual(column, placement.Column);
        Assert.AreEqual(row, placement.Row);
        Assert.AreEqual(columnSpan, placement.ColumnSpan);
        Assert.AreEqual(rowSpan, placement.RowSpan);
    }

    private static bool Overlaps(CardPlacement left, CardPlacement right) =>
        left.Column < right.RightExclusive &&
        right.Column < left.RightExclusive &&
        left.Row < right.BottomExclusive &&
        right.Row < left.BottomExclusive;
}
