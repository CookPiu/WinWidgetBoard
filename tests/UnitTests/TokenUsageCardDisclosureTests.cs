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
/// reading costs about 23 DIP (26 with a meter), the page tabs 30, the two tiles 60 and the
/// split meter 25. The spend curve is a backdrop and costs nothing.
/// </summary>
[TestClass]
public sealed class TokenUsageCardDisclosureTests
{
    [TestMethod(DisplayName =
        "UT-TOKUSE-090 [USE-015] Readings take what is left after the spend and the tabs")]
    // One row is 98 DIP. The spend costs 54 of it everywhere, so a one-cell card has 44 left
    // and fits one reading; add the page tabs at two cells across and 14 remain, which fits
    // none - the card is then its title, the tabs and the number, which is what it is for.
    [DataRow(CardSize.S, 1)]
    [DataRow(CardSize.M, 0)]
    [DataRow(CardSize.W, 0)]
    // 266 less the spend, the tabs, the tiles and the split leaves 97: four readings, which
    // is every reading left once the responses have moved into their tile.
    [DataRow(CardSize.L, 4)]
    [DataRow(CardSize.XL, 4)]
    public void MetricLimitFollowsCardHeight(CardSize size, int expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        Assert.AreEqual(expected, card.TokenUsageMetricLimit);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-091 [USE-015] The tiles and the split meter need two rows; the curve needs none")]
    [DataRow(CardSize.S, false)]
    [DataRow(CardSize.M, false)]
    [DataRow(CardSize.W, false)]
    [DataRow(CardSize.L, true)]
    [DataRow(CardSize.XL, true)]
    public void TilesAndSplitNeedTwoRows(CardSize size, bool expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        // The tiles and the split are the tallest blocks after the headline and the only ones
        // whose absence costs no reading, so they are what a one-row card gives up. The curve
        // sits behind the headline and is shown wherever the headline is.
        Assert.AreEqual(expected, card.IsTokenUsageKpiVisible);
        Assert.AreEqual(expected, card.IsTokenUsageSplitVisible);
        Assert.IsTrue(card.IsTokenUsageSpendCurveVisible);
        Assert.AreEqual(11, card.TokenUsageSpendCurve.Count);
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
