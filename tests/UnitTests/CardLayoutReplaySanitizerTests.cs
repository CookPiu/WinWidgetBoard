using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardLayoutReplaySanitizerTests
{
    [TestMethod(DisplayName = "UT-GRID-030 [LYT-006] Excessive persisted row gap falls back to compact placement")]
    public void ExcessivePersistedRowGapFallsBackToCompactPlacement()
    {
        CardLayoutItem[] persistedItems =
        [
            new("demo.notes", CardSize.L, 0, 0),
            new("demo.calendar", CardSize.M, 2, 0),
            new("demo.todo", CardSize.M, 2, 1),
            new("demo.timer", CardSize.M, 0, 7),
        ];

        CardLayoutReplayResult result =
            CardLayoutReplaySanitizer.Normalize(4, persistedItems);
        IReadOnlyList<CardPlacement> placements =
            ResponsiveGridLayout.Pack(4, result.Items);

        Assert.AreEqual(1, result.RecoveredItemCount);
        Assert.IsNull(result.Items.Single(
            item => item.InstanceId == "demo.timer").PreferredRow);
        CardPlacement timer = placements.Single(
            placement => placement.InstanceId == "demo.timer");
        Assert.AreEqual(0, timer.Column);
        Assert.AreEqual(2, timer.Row);
    }

    [TestMethod(DisplayName = "UT-GRID-031 [LYT-006] Bounded saved gap remains replayable")]
    public void BoundedSavedGapRemainsReplayable()
    {
        CardLayoutItem[] persistedItems =
        [
            new("lower", CardSize.M, 0, 3),
            new("upper", CardSize.S, 3, 0),
        ];

        CardLayoutReplayResult result =
            CardLayoutReplaySanitizer.Normalize(4, persistedItems);
        IReadOnlyList<CardPlacement> placements =
            ResponsiveGridLayout.Pack(4, result.Items);

        Assert.AreEqual(0, result.RecoveredItemCount);
        Assert.AreEqual(3, result.Items[0].PreferredRow);
        Assert.AreEqual(3, placements[0].Row);
    }
}
