using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardGridRealizationPlannerTests
{
    [TestMethod(DisplayName = "UT-GRID-040 [LYT-001/NFR-PERF-006] Planner realizes only cards intersecting the viewport")]
    public void PlannerRealizesOnlyCardsIntersectingViewport()
    {
        CardPlacement[] placements = [
            Placement("first", row: 0),
            Placement("second", row: 1),
            Placement("third", row: 2),
            Placement("fourth", row: 3),
        ];

        IReadOnlyList<int> result =
            CardGridRealizationPlanner.GetRealizedIndices(
                placements,
                rowHeight: 100,
                rowGap: 10,
                realizationTop: 110,
                realizationHeight: 100);

        int[] expected = [1];
        CollectionAssert.AreEqual(expected, result.ToArray());
    }

    [TestMethod(DisplayName = "UT-GRID-041 [LYT-002/NFR-PERF-006] Spanning cards stay realized while any row intersects")]
    public void SpanningCardsStayRealizedWhileAnyRowIntersects()
    {
        CardPlacement[] placements = [
            Placement("large", row: 0, rowSpan: 2),
            Placement("below", row: 2),
        ];

        IReadOnlyList<int> result =
            CardGridRealizationPlanner.GetRealizedIndices(
                placements,
                rowHeight: 100,
                rowGap: 10,
                realizationTop: 150,
                realizationHeight: 40);

        int[] expected = [0];
        CollectionAssert.AreEqual(expected, result.ToArray());
    }

    [TestMethod(DisplayName = "UT-GRID-042 [LYT-004/NFR-PERF-006] Recommended anchor remains realized outside the viewport")]
    public void RecommendedAnchorRemainsRealizedOutsideViewport()
    {
        CardPlacement[] placements = [
            Placement("first", row: 0),
            Placement("second", row: 1),
            Placement("anchor", row: 5),
        ];

        IReadOnlyList<int> result =
            CardGridRealizationPlanner.GetRealizedIndices(
                placements,
                rowHeight: 100,
                rowGap: 10,
                realizationTop: 0,
                realizationHeight: 100,
                recommendedAnchorIndex: 2);

        int[] expected = [0, 2];
        CollectionAssert.AreEqual(expected, result.ToArray());
    }

    [TestMethod(DisplayName = "UT-GRID-043 [LYT-001] Invalid startup viewport safely realizes the initial snapshot")]
    public void InvalidStartupViewportSafelyRealizesInitialSnapshot()
    {
        CardPlacement[] placements = [
            Placement("first", row: 0),
            Placement("second", row: 1),
            Placement("third", row: 2),
        ];

        IReadOnlyList<int> result =
            CardGridRealizationPlanner.GetRealizedIndices(
                placements,
                rowHeight: 100,
                rowGap: 10,
                realizationTop: 0,
                realizationHeight: 0);

        int[] expected = [0, 1, 2];
        CollectionAssert.AreEqual(expected, result.ToArray());
    }

    [TestMethod(DisplayName = "UT-GRID-044 [LYT-001/NFR-PERF-006] One hundred cards keep viewport realization bounded")]
    public void OneHundredCardsKeepViewportRealizationBounded()
    {
        CardPlacement[] placements = Enumerable
            .Range(0, 100)
            .Select(index => Placement($"card-{index}", row: index))
            .ToArray();

        IReadOnlyList<int> result =
            CardGridRealizationPlanner.GetRealizedIndices(
                placements,
                rowHeight: 100,
                rowGap: 10,
                realizationTop: 50 * 110,
                realizationHeight: 3 * 110);

        int[] expected = [50, 51, 52];
        CollectionAssert.AreEqual(expected, result.ToArray());
    }

    private static CardPlacement Placement(
        string id,
        int row,
        int rowSpan = 1) =>
        new(
            id,
            CardSize.M,
            Column: 0,
            Row: row,
            ColumnSpan: 2,
            RowSpan: rowSpan);
}
