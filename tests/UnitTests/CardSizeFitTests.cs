using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// What every card shows at every size it can be.
///
/// A card is a fixed number of grid rows tall and clips what does not fit rather than
/// scrolling it, so a section that overflows is not cramped - it is invisible, along with
/// everything laid out below it. The weather and token usage cards already stated their own
/// ladder; these are the ones that did not, plus the rule that a card's own controls step
/// aside while the layout is being edited.
/// </summary>
[TestClass]
public sealed class CardSizeFitTests
{
    // The shape the broker actually sends: a metrics array in the order the user configured,
    // each row already formatted by the provider.
    private const string SystemMonitorPayload = """
        {
          "sampledAtUtc": "2026-08-31T06:15:00Z",
          "metrics": [
            { "metricId": "cpu.usage", "iconId": "cpu", "status": "ready", "detail": "detailed", "primaryText": "27%", "secondaryText": "", "ratio": 0.27 },
            { "metricId": "memory.usage", "iconId": "memory", "status": "ready", "detail": "detailed", "primaryText": "65%", "secondaryText": "20.5 GB / 31.4 GB", "ratio": 0.65 },
            { "metricId": "gpu.usage", "iconId": "gpu", "status": "ready", "detail": "detailed", "primaryText": "34%", "secondaryText": "", "ratio": 0.34 },
            { "metricId": "net.down", "iconId": "network", "status": "ready", "detail": "compact", "primaryText": "1.5 KB/s", "secondaryText": "", "ratio": null },
            { "metricId": "net.up", "iconId": "network", "status": "ready", "detail": "compact", "primaryText": "308 B/s", "secondaryText": "", "ratio": null },
            { "metricId": "cpu.clock", "iconId": "cpu", "status": "ready", "detail": "compact", "primaryText": "4.48 GHz", "secondaryText": "", "ratio": null }
          ]
        }
        """;

    [TestMethod(DisplayName =
        "UT-CARD-080 [CRD-004/SYS-001] A one-row card shows only the readings it is tall enough for")]
    public async Task AOneRowCardShowsOnlyTheReadingsItIsTallEnoughFor()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateItem(
            noteEditor,
            BuiltInCardCatalog.SystemMonitorInstanceId,
            CardSize.L);
        ApplySystemMonitor(item);

        // The first reading is the card's headline and is drawn as one, so the rows under it
        // are the other five. Two rows of grid has room for all of them.
        Assert.AreEqual(SystemMonitorContract.CpuUsage, item.SystemMonitorHeadline?.MetricId);
        Assert.AreEqual(5, VisibleMetrics(item));

        // One row is about 98 DIP of content after the padding, the header and its spacing.
        // Readings sit two to a grid row and the taller half pays for both: a reading is 52
        // with its window band, and memory carries a line of detail, so its row is 70.
        // Stacked, the headline takes 60 of the 98 and that first row no longer fits in what
        // is left - and the list stops there rather than skipping past it, which would re-rank
        // the user's list.
        foreach (CardSize size in new[] { CardSize.S, CardSize.M })
        {
            item.UpdatePlacement(Place(BuiltInCardCatalog.SystemMonitorInstanceId, size));
            Assert.AreEqual(0, VisibleMetrics(item), $"{size} shows the wrong count.");
        }

        // Four cells across, one tall: the headline moves beside the readings and costs them
        // nothing, so the same 98 DIP carries memory + GPU (70) and stops before the network
        // rates would take it past the clip.
        item.UpdatePlacement(Place(BuiltInCardCatalog.SystemMonitorInstanceId, CardSize.W));
        Assert.AreEqual(2, VisibleMetrics(item));

        item.UpdatePlacement(
            Place(BuiltInCardCatalog.SystemMonitorInstanceId, CardSize.XL));
        Assert.AreEqual(5, VisibleMetrics(item));
    }

    [TestMethod(DisplayName =
        "UT-CARD-081 [CRD-004/SYS-001] Held-back readings are the ones the user ranked last")]
    public async Task HeldBackReadingsAreTheOnesRankedLast()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateItem(
            noteEditor,
            BuiltInCardCatalog.SystemMonitorInstanceId,
            CardSize.W);
        ApplySystemMonitor(item);

        // The broker sends the list in the order the user configured, so a card that cannot
        // show all of them keeps the top of that list rather than an arbitrary subset - and
        // it stops at the first reading that does not fit rather than skipping over it to a
        // shorter one behind, which would quietly re-rank the user's list.
        SystemMonitorMetricViewModel[] shown = item.SystemMonitorMetrics
            .Where(metric => metric.IsWithinCardLimit)
            .ToArray();
        Assert.AreEqual(2, shown.Length);
        CollectionAssert.AreEqual(
            item.SystemMonitorMetrics.Take(2).ToArray(),
            shown);
    }

    [TestMethod(DisplayName =
        "UT-CARD-085 [CRD-004/SYS-001] Readings pair up two to a row, and the taller half pays")]
    public async Task ReadingsPairUpTwoToARow()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateItem(
            noteEditor,
            BuiltInCardCatalog.SystemMonitorInstanceId,
            CardSize.L);
        ApplySystemMonitor(item);

        // Five readings under the headline become three rows, read left to right then down -
        // filling a column top to bottom first would put the user's second choice halfway down
        // the card. The odd one out ends a column rather than leaving a hole.
        Assert.AreEqual(3, item.SystemMonitorMetricPairs.Count);
        Assert.AreSame(item.SystemMonitorMetrics[0], item.SystemMonitorMetricPairs[0].Left);
        Assert.AreSame(item.SystemMonitorMetrics[1], item.SystemMonitorMetricPairs[0].Right);
        Assert.AreSame(item.SystemMonitorMetrics[4], item.SystemMonitorMetricPairs[2].Left);
        Assert.IsNull(item.SystemMonitorMetricPairs[2].Right);

        // Both halves share one grid row, so memory's line of detail costs the row it is on
        // its extra 18 DIP even though the GPU reading beside it has none: that row is 70 of
        // the one-row card's 98, and the next one does not fit.
        item.UpdatePlacement(Place(BuiltInCardCatalog.SystemMonitorInstanceId, CardSize.W));
        Assert.AreEqual(2, VisibleMetrics(item));
        Assert.IsFalse(item.SystemMonitorMetrics[2].IsWithinCardLimit);
    }

    [TestMethod(DisplayName =
        "UT-CARD-082 [CRD-004/SYS-001] Resizing republishes the reading limit")]
    public async Task ResizingRepublishesTheReadingLimit()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateItem(
            noteEditor,
            BuiltInCardCatalog.SystemMonitorInstanceId,
            CardSize.S);
        ApplySystemMonitor(item);
        Assert.AreEqual(0, VisibleMetrics(item));
        Assert.IsFalse(item.IsSystemMonitorWideLayout);

        // Growing the card has to bring the rest back without waiting for the next sample:
        // the rows update in place twice a second, and neither the limit nor the cells the
        // headline and the list occupy are part of a snapshot.
        item.UpdatePlacement(Place(BuiltInCardCatalog.SystemMonitorInstanceId, CardSize.L));

        Assert.AreEqual(5, VisibleMetrics(item));

        item.UpdatePlacement(Place(BuiltInCardCatalog.SystemMonitorInstanceId, CardSize.XL));
        Assert.IsTrue(item.IsSystemMonitorWideLayout);
        Assert.AreEqual(0, item.SystemMonitorMetricsRow, "Beside the headline, not under it.");
        Assert.AreEqual(1, item.SystemMonitorMetricsColumn);
    }

    [TestMethod(DisplayName =
        "UT-CARD-083 [CRD-004/LYT-003] A card's own controls step aside in edit mode")]
    public async Task ACardsOwnControlsStepAsideInEditMode()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(BuiltInCardCatalog.NotesInstanceId, CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var item = new CardSurfaceItem(
            Place(BuiltInCardCatalog.NotesInstanceId, CardSize.L),
            noteEditor,
            new NoteSearchViewModel(null),
            editMode,
            runtimeResourceResolver: static key => key);

        Assert.IsTrue(item.AreCardActionsVisible);
        Assert.IsTrue(item.AreNoteToolsVisible);

        // Edit mode is about where a card sits and how big it is, not what it is configured
        // to show - and the remove badge lands on the very corner a card puts its gear.
        editMode.BeginEdit();
        Assert.IsFalse(item.AreCardActionsVisible);
        Assert.IsFalse(item.AreNoteToolsVisible);

        editMode.CommitEdit();
        Assert.IsTrue(item.AreCardActionsVisible);
        Assert.IsTrue(item.AreNoteToolsVisible);
    }

    [TestMethod(DisplayName =
        "UT-CARD-084 [CRD-004/NTE-001] The one-cell note card is the note, without its tools")]
    public async Task TheOneCellNoteCardIsTheNoteWithoutItsTools()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateItem(
            noteEditor,
            BuiltInCardCatalog.NotesInstanceId,
            CardSize.S);

        // Four icon buttons beside the title leave a one-cell card with no room for the note
        // it exists to show. Every one of them is reachable from a larger card.
        Assert.IsFalse(item.AreNoteToolsVisible);

        item.UpdatePlacement(Place(BuiltInCardCatalog.NotesInstanceId, CardSize.M));
        Assert.IsTrue(item.AreNoteToolsVisible);
    }

    private static int VisibleMetrics(CardSurfaceItem item) =>
        item.SystemMonitorMetrics.Count(metric => metric.IsWithinCardLimit);

    private static CardSurfaceItem CreateItem(
        NoteEditorViewModel noteEditor,
        string instanceId,
        CardSize size)
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(instanceId, size)]);
        return new CardSurfaceItem(
            Place(instanceId, size),
            noteEditor,
            new NoteSearchViewModel(null),
            new CardLayoutEditViewModel(layout),
            runtimeResourceResolver: static key => key);
    }

    private static CardPlacement Place(string instanceId, CardSize size)
    {
        CardSpan span = ResponsiveGridLayout.GetSpan(size);
        return new CardPlacement(instanceId, size, 0, 0, span.Columns, span.Rows);
    }

    private static void ApplySystemMonitor(CardSurfaceItem item)
    {
        using JsonDocument document = JsonDocument.Parse(SystemMonitorPayload);
        Assert.IsTrue(
            item.Runtime.ApplyRemoteSnapshot(
                new CardRuntimeSnapshot(
                    BuiltInCardCatalog.SystemMonitorInstanceId,
                    BuiltInCardCatalog.SystemMonitorCardTypeId,
                    BuiltInCardRuntimeFactory.CurrentSchemaVersion,
                    sequence: 1,
                    new DateTimeOffset(2026, 8, 31, 6, 15, 0, TimeSpan.Zero),
                    CardRuntimeFreshness.Fresh,
                    CardRuntimeStatus.Ready,
                    document.RootElement.Clone())));
    }
}
