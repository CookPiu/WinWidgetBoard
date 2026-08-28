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
/// reading costs about 23 DIP (26 with a meter), the page tabs 30, the trend 38, the
/// breakdown 30 and the quota about 46.
/// </summary>
[TestClass]
public sealed class TokenUsageCardDisclosureTests
{
    [TestMethod(DisplayName =
        "UT-TOKUSE-090 [USE-015] Each card size shows as many readings as it can hold")]
    [DataRow(CardSize.S, 2)]
    [DataRow(CardSize.M, 2)]
    [DataRow(CardSize.W, 2)]
    [DataRow(CardSize.L, 5)]
    [DataRow(CardSize.XL, 5)]
    public void MetricLimitFollowsCardHeight(CardSize size, int expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);

        Assert.AreEqual(expected, card.TokenUsageMetricLimit);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-091 [USE-015] A short card drops the trend before it drops a reading")]
    [DataRow(CardSize.S, false)]
    [DataRow(CardSize.M, false)]
    [DataRow(CardSize.W, false)]
    [DataRow(CardSize.L, true)]
    [DataRow(CardSize.XL, true)]
    public void TrendNeedsTwoRows(CardSize size, bool expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        // The trend is the tallest single block and the only one whose absence costs no
        // number, so it is the first thing a one-row card gives up.
        Assert.AreEqual(expected, card.IsTokenUsageTrendVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-092 [USE-015] Nothing is hidden at the card's default size")]
    public void EverythingFitsAtTheDefaultSize()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.L);
        ApplyReadyPayload(card);

        // 266 DIP, less the tabs, the amount and the trend, leaves room for every reading -
        // so L shows the whole card rather than a prefix of it.
        Assert.IsTrue(card.IsTokenUsageCostVisible);
        Assert.IsTrue(card.IsTokenUsageTrendVisible);
        Assert.AreEqual(TokenUsageContract.MetricIds.Count, card.TokenUsageMetrics.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-097 [USE-017] The amount is the headline, and needs two rows to appear")]
    [DataRow(CardSize.S, false)]
    [DataRow(CardSize.M, false)]
    [DataRow(CardSize.W, false)]
    [DataRow(CardSize.L, true)]
    [DataRow(CardSize.XL, true)]
    public void AmountNeedsTwoRows(CardSize size, bool expected)
    {
        using CardSurfaceItem card = CreateTokenUsageCard(size);
        ApplyReadyPayload(card);

        Assert.AreEqual(expected, card.IsTokenUsageCostVisible);
        // The figure itself is available at every size; only the headline treatment needs
        // the height.
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
            TokenUsageContract.MetricIds.ToArray(),
            card.TokenUsageMetrics.Select(metric => metric.MetricId).ToArray());
        // Spend first: the billed total and the cache reads that dominate it are the two a
        // card of any size keeps.
        Assert.AreEqual(TokenUsageContract.TodayBilledTokens, card.TokenUsageMetrics[0].MetricId);
        Assert.AreEqual(
            TokenUsageContract.TodayCacheReadTokens,
            card.TokenUsageMetrics[1].MetricId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-096 [USE-015] Resizing republishes the readings without a new snapshot")]
    public void ResizingRebuildsTheVisibleReadings()
    {
        using CardSurfaceItem card = CreateTokenUsageCard(CardSize.L);
        ApplyReadyPayload(card);
        Assert.AreEqual(TokenUsageContract.MetricIds.Count, card.TokenUsageMetrics.Count);

        // Resizing raises Placement only. A card grown to hold every reading has to fill in
        // the ones it was hiding, rather than waiting for the next refresh to land.
        card.UpdatePlacement(
            new CardPlacement(card.InstanceId, CardSize.XL, 0, 0, 4, 2));

        Assert.AreEqual(TokenUsageContract.MetricIds.Count, card.TokenUsageMetrics.Count);
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
