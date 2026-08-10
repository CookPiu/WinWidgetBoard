using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardDragPlacementProjectorTests
{
    [TestMethod(DisplayName = "UT-GRID-017 [LYT-004] Projection reflows an occupied target")]
    public void ProjectionReflowsOccupiedTarget()
    {
        var items = new[]
        {
            new CardLayoutItem("first", CardSize.M),
            new CardLayoutItem("second", CardSize.M),
            new CardLayoutItem("dragged", CardSize.S),
        };
        IReadOnlyList<CardPlacement> current = ResponsiveGridLayout.Pack(4, items);

        Assert.IsTrue(CardDragPlacementProjector.TryProject(
            4,
            items,
            current,
            "dragged",
            new GridCell(3, 0),
            out IReadOnlyList<CardPlacement> projected));

        CardPlacement dragged = projected.Single(
            placement => placement.InstanceId == "dragged");
        Assert.AreEqual(3, dragged.Column);
        Assert.AreEqual(0, dragged.Row);
        CardPlacement first = projected.Single(
            placement => placement.InstanceId == "first");
        Assert.AreEqual(current[0], first);
        CardPlacement displaced = projected.Single(
            placement => placement.InstanceId == "second");
        Assert.AreEqual(0, displaced.Column);
        Assert.AreEqual(1, displaced.Row);
        AssertNoOverlapAndWithinBounds(4, projected);
    }

    [TestMethod(DisplayName = "UT-GRID-018 [LYT-004] Projection clamps an out-of-bounds request")]
    public void ProjectionClampsOutOfBoundsRequest()
    {
        var items = new[]
        {
            new CardLayoutItem("first", CardSize.M),
            new CardLayoutItem("second", CardSize.M),
            new CardLayoutItem("dragged", CardSize.S),
        };
        IReadOnlyList<CardPlacement> current = ResponsiveGridLayout.Pack(4, items);

        Assert.IsTrue(CardDragPlacementProjector.TryProject(
            4,
            items,
            current,
            "dragged",
            new GridCell(99, -10),
            out IReadOnlyList<CardPlacement> projected));

        CardPlacement dragged = projected.Single(
            placement => placement.InstanceId == "dragged");
        Assert.AreEqual(3, dragged.Column);
        Assert.AreEqual(0, dragged.Row);
        AssertNoOverlapAndWithinBounds(4, projected);
    }

    [TestMethod(DisplayName = "UT-GRID-019 [LYT-004] Edit mode gates drag projection")]
    public void EditModeGatesDragProjection()
    {
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("first", CardSize.M),
                new CardLayoutItem("dragged", CardSize.S),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);

        Assert.IsFalse(editMode.TryPreviewDrop(
            "dragged",
            new GridCell(3, 0),
            out IReadOnlyList<CardPlacement> blocked));
        Assert.AreEqual(0, blocked.Count);

        editMode.BeginEdit();

        Assert.IsTrue(editMode.TryPreviewDrop(
            "dragged",
            new GridCell(3, 0),
            out IReadOnlyList<CardPlacement> projected));
        Assert.AreNotEqual(
            layout.Placements.Single(placement => placement.InstanceId == "dragged"),
            projected.Single(placement => placement.InstanceId == "dragged"));
    }

    [TestMethod(DisplayName = "UT-GRID-025 [LYT-004] Reflow moves an occupant into the vacated cell")]
    public void ReflowMovesOccupantIntoVacatedCell()
    {
        var items = new[]
        {
            new CardLayoutItem("demo.notes", CardSize.L),
            new CardLayoutItem("demo.timer", CardSize.M),
            new CardLayoutItem("demo.todo", CardSize.M),
            new CardLayoutItem("demo.calendar", CardSize.M),
        };
        IReadOnlyList<CardPlacement> current = ResponsiveGridLayout.Pack(4, items);
        foreach ((string draggedId, string displacedId) in new[]
        {
            ("demo.timer", "demo.calendar"),
            ("demo.calendar", "demo.timer"),
        })
        {
            CardPlacement draggedStart = current.Single(
                placement => placement.InstanceId == draggedId);
            CardPlacement displacedStart = current.Single(
                placement => placement.InstanceId == displacedId);

            Assert.IsTrue(CardDragPlacementProjector.TryProject(
                4,
                items,
                current,
                draggedId,
                new GridCell(displacedStart.Column, displacedStart.Row),
                out IReadOnlyList<CardPlacement> projected));

            CardPlacement dragged = projected.Single(
                placement => placement.InstanceId == draggedId);
            Assert.AreEqual(displacedStart.Column, dragged.Column);
            Assert.AreEqual(displacedStart.Row, dragged.Row);
            CardPlacement displaced = projected.Single(
                placement => placement.InstanceId == displacedId);
            Assert.AreEqual(draggedStart.Column, displaced.Column);
            Assert.AreEqual(draggedStart.Row, displaced.Row);
            AssertNoOverlapAndWithinBounds(4, projected);
        }
    }

    [TestMethod(DisplayName = "UT-GRID-026 [LYT-004] Every demo card can project across occupied cells")]
    public void EveryDemoCardCanProjectAcrossOccupiedCells()
    {
        var items = new[]
        {
            new CardLayoutItem("demo.notes", CardSize.L),
            new CardLayoutItem("demo.timer", CardSize.M),
            new CardLayoutItem("demo.todo", CardSize.M),
            new CardLayoutItem("demo.calendar", CardSize.M),
        };
        IReadOnlyList<CardPlacement> current = ResponsiveGridLayout.Pack(4, items);
        int bottomExclusive = current.Max(placement => placement.BottomExclusive);

        foreach (CardLayoutItem item in items)
        {
            CardSpan span = ResponsiveGridLayout.GetSpan(item.Size);
            int maxColumn = 4 - Math.Min(span.Columns, 4);
            for (int row = 0; row <= bottomExclusive; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    Assert.IsTrue(CardDragPlacementProjector.TryProject(
                        4,
                        items,
                        current,
                        item.InstanceId,
                        new GridCell(column, row),
                        out IReadOnlyList<CardPlacement> projected));

                    CardPlacement dragged = projected.Single(
                        placement => placement.InstanceId == item.InstanceId);
                    Assert.AreEqual(Math.Min(column, maxColumn), dragged.Column);
                    Assert.AreEqual(row, dragged.Row);
                    AssertNoOverlapAndWithinBounds(4, projected);
                }
            }
        }
    }

    private static void AssertNoOverlapAndWithinBounds(
        int columnCount,
        IReadOnlyList<CardPlacement> placements)
    {
        for (int index = 0; index < placements.Count; index++)
        {
            CardPlacement placement = placements[index];
            Assert.IsTrue(placement.Column >= 0);
            Assert.IsTrue(placement.Row >= 0);
            Assert.IsTrue(placement.RightExclusive <= columnCount);

            for (int otherIndex = index + 1;
                otherIndex < placements.Count;
                otherIndex++)
            {
                CardPlacement other = placements[otherIndex];
                bool overlaps = placement.Column < other.RightExclusive &&
                    other.Column < placement.RightExclusive &&
                    placement.Row < other.BottomExclusive &&
                    other.Row < placement.BottomExclusive;
                Assert.IsFalse(overlaps);
            }
        }
    }
}
