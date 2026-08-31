using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The resize drag is direct manipulation over a discrete model: the pointer moves in DIP,
/// the layout stores one of five size ids. These pin the translation between the two.
/// </summary>
[TestClass]
public sealed class CardResizeCalculatorTests
{
    // A four-column board 800 DIP wide: cell 194, column stride 202, row stride 168.
    private const int ColumnCount = 4;
    private const double AvailableWidth = 800;
    private const double ColumnGap = 8;
    private const double RowHeight = 160;
    private const double RowGap = 8;

    private static CardSize Resize(
        CardSize size,
        double offsetX,
        double offsetY,
        bool columns = true,
        bool rows = true,
        int columnCount = ColumnCount)
    {
        CardSpan span = ResponsiveGridLayout.GetSpan(size);
        var placement = new CardPlacement(
            "demo.card",
            size,
            Column: 0,
            Row: 0,
            span.Columns,
            span.Rows);
        return CardResizeCalculator.GetResizeTarget(
            columnCount,
            AvailableWidth,
            ColumnGap,
            RowHeight,
            RowGap,
            placement,
            offsetX,
            offsetY,
            columns,
            rows);
    }

    [TestMethod(DisplayName =
        "UT-GRID-060 [LYT-003] A resize that moves nothing keeps the size it started at")]
    public void AResizeThatMovesNothingKeepsTheSize()
    {
        // The press itself must not resize: a handle can be grabbed and released.
        Assert.AreEqual(CardSize.S, Resize(CardSize.S, 0, 0));
        Assert.AreEqual(CardSize.M, Resize(CardSize.M, 0, 0));
        Assert.AreEqual(CardSize.XL, Resize(CardSize.XL, 0, 0));
    }

    [TestMethod(DisplayName =
        "UT-GRID-061 [LYT-003] Dragging east crosses the column steps 1, 2 and 4")]
    public void DraggingEastCrossesTheColumnSteps()
    {
        // A column stride here is 202 DIP; half of it is where the next step wins.
        Assert.AreEqual(CardSize.S, Resize(CardSize.S, 60, 0, rows: false));
        Assert.AreEqual(CardSize.M, Resize(CardSize.S, 140, 0, rows: false));
        Assert.AreEqual(CardSize.M, Resize(CardSize.S, 300, 0, rows: false));
        // Three columns is not a size. It sits exactly between two and four, and the tie
        // resolves down, so reaching W takes a drag that is nearly four columns wide.
        Assert.AreEqual(CardSize.M, Resize(CardSize.S, 460, 0, rows: false));
        Assert.AreEqual(CardSize.W, Resize(CardSize.S, 600, 0, rows: false));
        Assert.AreEqual(CardSize.W, Resize(CardSize.S, 900, 0, rows: false));
    }

    [TestMethod(DisplayName =
        "UT-GRID-062 [LYT-003] Dragging west shrinks, and a tie goes to the narrower card")]
    public void DraggingWestShrinks()
    {
        Assert.AreEqual(CardSize.M, Resize(CardSize.W, -300, 0, rows: false));
        Assert.AreEqual(CardSize.S, Resize(CardSize.M, -140, 0, rows: false));
        // Exactly three columns is equidistant from two and four. Overshooting is the mistake
        // a user corrects by pulling back, so the tie has to resolve toward the smaller card.
        Assert.AreEqual(CardSize.M, Resize(CardSize.W, -196, 0, rows: false));
    }

    [TestMethod(DisplayName =
        "UT-GRID-063 [LYT-003] Dragging south adds the second row, and stops there")]
    public void DraggingSouthAddsTheSecondRow()
    {
        Assert.AreEqual(CardSize.L, Resize(CardSize.M, 0, 120, columns: false));
        Assert.AreEqual(CardSize.XL, Resize(CardSize.W, 0, 120, columns: false));
        // There is no three-row card; dragging further must not invent one.
        Assert.AreEqual(CardSize.XL, Resize(CardSize.W, 0, 900, columns: false));
        Assert.AreEqual(CardSize.M, Resize(CardSize.L, 0, -120, columns: false));
    }

    [TestMethod(DisplayName =
        "UT-GRID-064 [LYT-003] A one-column card dragged taller becomes L, not a size that has no id")]
    public void AOneColumnCardDraggedTallerBecomesL()
    {
        // S is 1x1 and there is no 1x2. Refusing to grow would leave the handle dead, so the
        // card takes the narrowest two-row size that exists.
        Assert.AreEqual(CardSize.L, Resize(CardSize.S, 0, 120, columns: false));
        Assert.AreEqual(CardSize.L, Resize(CardSize.S, 20, 120));
    }

    [TestMethod(DisplayName =
        "UT-GRID-065 [LYT-003] A card never grows wider than the board it is on")]
    public void ACardNeverGrowsWiderThanTheBoard()
    {
        // Two columns is the small breakpoint. Pack would clamp a four-wide card to the grid,
        // so offering W there would state a size the user cannot see.
        Assert.AreEqual(CardSize.M, Resize(CardSize.M, 900, 0, rows: false, columnCount: 2));
        Assert.AreEqual(CardSize.L, Resize(CardSize.M, 900, 120, columnCount: 2));
    }

    [TestMethod(DisplayName =
        "UT-GRID-066 [LYT-003] Each handle only moves its own axis")]
    public void EachHandleOnlyMovesItsOwnAxis()
    {
        // The east handle is dragged diagonally: the vertical component must be ignored, or
        // every horizontal resize would also change the card's height.
        Assert.AreEqual(CardSize.M, Resize(CardSize.S, 140, 400, rows: false));
        Assert.AreEqual(CardSize.L, Resize(CardSize.M, 900, 120, columns: false));
    }
}
