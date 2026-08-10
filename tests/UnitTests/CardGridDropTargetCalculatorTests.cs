using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardGridDropTargetCalculatorTests
{
    [TestMethod(DisplayName = "UT-GRID-022 [LYT-004] Drop target clamps a spanning card to the grid edges")]
    public void DropTargetClampsSpanningCardToGridEdges()
    {
        var placement = new CardPlacement(
            "dragged",
            CardSize.L,
            Column: 1,
            Row: 2,
            ColumnSpan: 2,
            RowSpan: 2);

        GridCell cell = CardGridDropTargetCalculator.GetDropCell(
            columnCount: 4,
            availableWidth: 1000,
            columnGap: 12,
            rowHeight: 180,
            rowGap: 12,
            placement,
            offsetX: 10000,
            offsetY: -10000);

        Assert.AreEqual(new GridCell(2, 0), cell);
    }

    [TestMethod(DisplayName = "UT-GRID-023 [LYT-004] Drop target uses the final release offset")]
    public void DropTargetUsesFinalReleaseOffset()
    {
        var placement = new CardPlacement(
            "dragged",
            CardSize.S,
            Column: 0,
            Row: 0,
            ColumnSpan: 1,
            RowSpan: 1);

        GridCell cell = CardGridDropTargetCalculator.GetDropCell(
            columnCount: 4,
            availableWidth: 1000,
            columnGap: 12,
            rowHeight: 180,
            rowGap: 12,
            placement,
            offsetX: 506,
            offsetY: 192);

        Assert.AreEqual(new GridCell(2, 1), cell);
    }
}
