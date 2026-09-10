using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// What the token usage card shows at each size.
///
/// This is not a styling preference. A card is a fixed number of grid rows tall and clips what
/// does not fit rather than scrolling it, so a block laid out past the bottom is invisible -
/// and so is everything after it. At L the readings alone consumed the row, which is how the
/// vendor breakdown and, on the Codex page, the quota ended up drawn off the card.
///
/// The budget these limits come from: a row is 160 DIP with an 8 DIP gap, so two rows give
/// 328; less 12 DIP padding twice, a 32 DIP header and 6 DIP spacing leaves 266 DIP. One
/// reading costs about 23 DIP (26 with a meter), the two tiles 60 and the split meter 25. The
/// spend curve is a backdrop and costs nothing, and the page tabs are no longer charged at
/// all: they sit in the card's header row beside the settings gear.
///
/// The four-column sizes spend that budget per lane rather than per card. W is four cells
/// across but one tall, so stacked it had less room for readings than the one-cell card;
/// beside the amount, each block gets the card's whole height.
/// </summary>
[TestClass]
public sealed class TokenUsageCardDisclosureTests
{
    [TestMethod(DisplayName =
        "UT-TOKUSE-090 [USE-015] Readings take the height their lane or their row leaves")]
    // One row is 98 DIP. The spend costs 54 of it everywhere, so a stacked one-row card has 44
    // left and fits one reading at either width - the tabs no longer take that reading away
    // from the two-cell card, because they are not in the content flow any more.
    [DataRow(CardSize.S, 1)]
    [DataRow(CardSize.M, 1)]
    // Four cells across, the readings have a lane to themselves and pay for nothing above
    // them: the whole 98 DIP, which is four readings.
    [DataRow(CardSize.W, 4)]
    // 266 less the spend, the tiles and the split leaves 127 - more readings than the page
    // has left once the responses have moved into their tile.
    [DataRow(CardSize.L, 5)]
    // Beside the amount again, less the band the expanded split takes underneath.
    [DataRow(CardSize.XL, 5)]
    public void MetricLimitFollowsCardHeight(CardSize size, int expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        Assert.AreEqual(expected, card.TokenUsageMetricLimit);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-091 [USE-015] The tiles need a lane or a row; the split has one form per size")]
    // The tiles are 60 DIP. Stacked that is a second grid row, which S and M do not have;
    // beside the amount they cost it nothing, which is what earns them the one-row W card.
    [DataRow(CardSize.S, false, false, false)]
    [DataRow(CardSize.M, false, false, false)]
    [DataRow(CardSize.W, true, false, false)]
    // L is the size the single meter was chosen for: one meter is all the room there is.
    [DataRow(CardSize.L, true, true, false)]
    // XL has a band under its lanes, so the same slices get a row each - and only one of the
    // two forms is ever drawn, or the card would state the split twice.
    [DataRow(CardSize.XL, true, false, true)]
    public void TilesAndSplitFollowTheArrangement(
        CardSize size,
        bool tiles,
        bool splitMeter,
        bool breakdownRows)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        Assert.AreEqual(tiles, card.IsTokenUsageKpiVisible);
        Assert.AreEqual(splitMeter, card.IsTokenUsageSplitVisible);
        Assert.AreEqual(breakdownRows, card.IsTokenUsageBreakdownVisible);
        // Rows are only built where they are drawn: the collection is the tree.
        Assert.AreEqual(breakdownRows ? 1 : 0, card.TokenUsageBreakdown.Count);
        // The curve sits behind the headline and is shown wherever the headline is.
        Assert.IsTrue(card.IsTokenUsageSpendCurveVisible);
        Assert.AreEqual(11, card.TokenUsageSpendCurve.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-131 [USE-015] A bigger card never shows less than a smaller one")]
    public void GrowingTheCardNeverShowsLess()
    {
        CardSize[] ladder = [CardSize.S, CardSize.M, CardSize.W, CardSize.L, CardSize.XL];
        var shown = new int[ladder.Length];
        for (int index = 0; index < ladder.Length; index++)
        {
            using CardSurfaceItem card = CreateTokenUsageCard(ladder[index]);
            ApplyReadyPayload(card);
            shown[index] = card.TokenUsageMetrics.Count +
                (card.IsTokenUsageKpiVisible ? 2 : 0) +
                (card.IsTokenUsageSplitVisible || card.IsTokenUsageBreakdownVisible ? 1 : 0);
        }

        // The ladder used to dip: the tabs cost the two-cell card the one reading the one-cell
        // card had, and the four-cell card - twice as wide and no shorter - showed the same
        // nothing. Growing a card is the user asking for more of it.
        for (int index = 1; index < ladder.Length; index++)
        {
            Assert.IsTrue(
                shown[index] >= shown[index - 1],
                $"{ladder[index]} shows {shown[index]} figures, fewer than {ladder[index - 1]}" +
                $" at {shown[index - 1]}.");
        }
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-132 [USE-015] The four-column sizes put their blocks beside the amount")]
    [DataRow(CardSize.S, false)]
    [DataRow(CardSize.M, false)]
    [DataRow(CardSize.W, true)]
    [DataRow(CardSize.L, false)]
    [DataRow(CardSize.XL, true)]
    public void WideSizesPlaceBlocksBesideTheAmount(CardSize size, bool wide)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        // One template, two sets of cells - the same arrangement the weather card's forecast
        // uses. Stacked, every block spans all three columns and takes a row of its own.
        Assert.AreEqual(wide, card.IsTokenUsageWideLayout);
        Assert.AreEqual(wide ? 1 : 3, card.TokenUsageHeadlineColumnSpan);
        Assert.AreEqual(wide ? 0 : 1, card.TokenUsageKpiRow);
        Assert.AreEqual(wide ? 1 : 0, card.TokenUsageKpiColumn);
        Assert.AreEqual(wide ? 1 : 3, card.TokenUsageKpiColumnSpan);
        Assert.AreEqual(wide ? 0 : 2, card.TokenUsageMetricsRow);
        Assert.AreEqual(wide ? 2 : 0, card.TokenUsageMetricsColumn);
        Assert.AreEqual(wide ? 1 : 3, card.TokenUsageMetricsColumnSpan);
        // An Auto lane is measured against infinity, so the amount is bounded where it has one
        // and unbounded where it spans the card.
        Assert.AreEqual(wide, !double.IsPositiveInfinity(card.TokenUsageHeadlineMaxWidth));
        Assert.AreEqual(wide, !double.IsPositiveInfinity(card.TokenUsageKpiMaxWidth));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-133 [USE-015] The one-cell card's readings take both number lanes")]
    public void SmallestCardDropsTheSecondaryLane()
    {
        using CardSurfaceItem small = CreateTokenUsageCard(CardSize.S);
        using CardSurfaceItem medium = CreateTokenUsageCard(CardSize.M);
        ApplyReadyPayload(small);
        ApplyReadyPayload(medium);

        TokenUsageMetricViewModel narrow = small.TokenUsageMetrics.Single();
        TokenUsageMetricViewModel wider = medium.TokenUsageMetrics.Single();

        // 130 DIP split 55/37/37 fits the name and one figure, not two.
        Assert.IsFalse(narrow.IsSecondaryVisible);
        Assert.AreEqual(2, narrow.PrimaryColumnSpan);
        Assert.IsTrue(wider.IsSecondaryVisible);
        Assert.AreEqual(1, wider.PrimaryColumnSpan);
        // A reader that is not looking at the card is not short of width.
        Assert.AreEqual(wider.AutomationName, narrow.AutomationName);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-134 [USE-015] The page tabs give way in edit mode with the gear")]
    public void PageTabsGiveWayInEditMode()
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(BuiltInCardCatalog.TokenUsageInstanceId, CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        var surface = new CardLayoutSurfaceViewModel(
            editMode,
            new NoteEditorViewModel(null),
            new NoteSearchViewModel(null));
        using CardSurfaceItem card = surface.Items.Single();
        ApplyReadyPayload(card);
        Assert.IsTrue(card.IsTokenUsagePageSwitcherVisible);

        // They sit beside the gear now, and edit mode is about where the card is and how big,
        // not about what it is configured to show.
        editMode.BeginEdit();

        Assert.IsFalse(card.IsTokenUsagePageSwitcherVisible);
        Assert.IsFalse(card.AreCardActionsVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-092 [USE-015] Nothing is hidden at the card's default size")]
    public void EverythingFitsAtTheDefaultSize()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.L);
        ApplyReadyPayload(card);

        // 266 DIP, less the tabs, the amount, the tiles and the split, leaves room for every
        // reading that is not already a tile - so L shows the whole card rather than a prefix
        // of it.
        Assert.IsTrue(card.IsTokenUsageCostVisible);
        Assert.IsTrue(card.IsTokenUsageKpiVisible);
        Assert.IsTrue(card.IsTokenUsageSplitVisible);
        Assert.AreEqual(TokenUsageContract.MetricIds.Count - 1, card.TokenUsageMetrics.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-126 [USE-015] Responses leave the rows once their tile shows them")]
    public void RequestsRowYieldsToTheTile()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.L);
        ApplyReadyPayload(card);

        // The same number twice on one card is the tile's, not the row's.
        Assert.AreEqual("279", card.TokenUsageRequestsText);
        Assert.IsFalse(card.TokenUsageMetrics.Any(metric =>
            metric.MetricId == TokenUsageContract.TodayRequests));
        Assert.AreEqual("claude-opus-5 \u2248$108.80", card.TokenUsageSplitLeadText);
        Assert.IsFalse(card.IsTokenUsageSplitTrailVisible);
        Assert.AreEqual(100d, card.TokenUsageSplitPercent, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-097 [USE-017] The amount is the headline at every size")]
    [DataRow(CardSize.S)]
    [DataRow(CardSize.M)]
    [DataRow(CardSize.W)]
    [DataRow(CardSize.L)]
    [DataRow(CardSize.XL)]
    public void AmountShowsAtEverySize(CardSize size)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        // It used to need two rows, which put a table of counts on the small cards and left
        // the number they are read for off them. The height is spent on the number first now,
        // and the readings are what give way.
        Assert.IsTrue(card.IsTokenUsageCostVisible);
        Assert.AreNotEqual(string.Empty, card.TokenUsageCostText);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-094 [USE-015] The smallest card has no room for the page tabs")]
    public void SmallestCardHidesThePageTabs()
    {
        using CardSurfaceItem small = CreateTokenUsageCard(CardSize.S);
        using CardSurfaceItem medium = CreateTokenUsageCard(CardSize.M);
        ApplyReadyPayload(small);
        ApplyReadyPayload(medium);

        Assert.IsFalse(small.IsTokenUsagePageSwitcherVisible);
        Assert.IsTrue(medium.IsTokenUsagePageSwitcherVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-095 [USE-015] Truncation keeps the readings worth seeing most")]
    public void TruncationKeepsThePriorityPrefix()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.L);
        ApplyReadyPayload(card);

        CollectionAssert.AreEqual(
            TokenUsageContract.MetricIds
                .Where(metricId => metricId != TokenUsageContract.TodayRequests)
                .ToArray(),
            card.TokenUsageMetrics.Select(metric => metric.MetricId).ToArray());
        // Spend first: the billed total is the one reading a card of any size keeps, and the
        // output that is the expensive part of it comes next.
        Assert.AreEqual(TokenUsageContract.TodayBilledTokens, card.TokenUsageMetrics[0].MetricId);
        Assert.AreEqual(
            TokenUsageContract.TodayOutputTokens,
            card.TokenUsageMetrics[1].MetricId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-096 [USE-015] Resizing republishes the readings without a new snapshot")]
    public void ResizingRebuildsTheVisibleReadings()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.S);
        ApplyReadyPayload(card);
        Assert.AreEqual(1, card.TokenUsageMetrics.Count);
        Assert.IsFalse(card.IsTokenUsageKpiVisible);

        // Resizing raises Placement only. A card grown to hold every reading has to fill in
        // the ones it was hiding, rather than waiting for the next refresh to land.
        card.UpdatePlacement(
            new CardPlacement(card.InstanceId, CardSize.XL, 0, 0, 4, 2));

        Assert.IsTrue(card.IsTokenUsageKpiVisible);
        Assert.AreEqual(TokenUsageContract.MetricIds.Count - 1, card.TokenUsageMetrics.Count);
        // The blocks that move on a resize move with them.
        Assert.IsTrue(card.IsTokenUsageWideLayout);
        Assert.AreEqual(1, card.TokenUsageBreakdown.Count);
    }

    private static void ApplyReadyPayload(CardSurfaceItem card)
    {
        card.Runtime.ApplySnapshot(
            new CardRuntimeSnapshot(
                card.InstanceId,
                card.CardTypeId,
                BuiltInCardRuntimeFactory.CurrentSchemaVersion,
                sequence: 1,
                new DateTimeOffset(2026, 8, 28, 10, 46, 0, TimeSpan.Zero),
                CardRuntimeFreshness.Fresh,
                CardRuntimeStatus.Ready,
                TokenUsageCardPayloads.ReadyWithQuota()));
    }

    private static CardSurfaceItem CreateTokenUsageCard(CardSize size)
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(BuiltInCardCatalog.TokenUsageInstanceId, size)]);
        var surface = new CardLayoutSurfaceViewModel(
            new CardLayoutEditViewModel(layout),
            new NoteEditorViewModel(null),
            new NoteSearchViewModel(null));
        return surface.Items.Single();
    }
}
