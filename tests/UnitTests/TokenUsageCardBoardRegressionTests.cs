using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// A board carrying every built-in card, which is what the panel actually shows once the token
/// usage card is added. The point is the interaction: every card runs every card type's
/// projection over its own payload, so a projection that mishandles a foreign payload takes
/// down the card it does not belong to. The symptom then shows up on the *other* cards - they
/// stop updating and sit at their initial state - which makes it easy to misread as a problem
/// with them.
/// </summary>
[TestClass]
public sealed class TokenUsageCardBoardRegressionTests
{
    private static readonly DateTimeOffset SampledAt =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-TOKUSE-080 [USE-014] Every built-in card builds and registers on one board")]
    public void FullBoardBuildsEveryCard()
    {
        using var scheduler = new CardRuntimeVisibilityScheduler();
        using CardLayoutSurfaceViewModel surface = CreateSurface(scheduler);

        Assert.AreEqual(BuiltInCardCatalog.Addable.Count, surface.Items.Count);
        // Registration is what drives a card out of its initial state. A card missing from the
        // scheduler never refreshes and stays where it started for as long as the panel runs.
        Assert.AreEqual(surface.Items.Count, scheduler.RegistrationCount);
        CollectionAssert.AreEquivalent(
            BuiltInCardCatalog.Addable.Select(card => card.InstanceId).ToArray(),
            surface.Items.Select(item => item.InstanceId).ToArray());
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-081 [USE-014] A card that is not token usage projects an empty usage view")]
    public void NonTokenCardsProjectEmptyUsageWithoutFailing()
    {
        using var scheduler = new CardRuntimeVisibilityScheduler();
        using CardLayoutSurfaceViewModel surface = CreateSurface(scheduler);

        foreach (CardSurfaceItem item in surface.Items)
        {
            if (string.Equals(
                    item.CardTypeId,
                    BuiltInCardCatalog.TokenUsageCardTypeId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // Every card runs the token-usage projection over its own payload, so it has to
            // degrade rather than throw on a payload that is not a usage report.
            Assert.AreEqual(0, item.TokenUsagePages.Count, item.InstanceId);
            Assert.IsFalse(item.HasTokenUsageData, item.InstanceId);
            Assert.IsNull(item.TokenUsageCurrentPage, item.InstanceId);
        }
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-082 [USE-014] Selecting a page on a card without pages is a no-op")]
    public void SelectingAPageWithoutPagesDoesNotThrow()
    {
        using var scheduler = new CardRuntimeVisibilityScheduler();
        using CardLayoutSurfaceViewModel surface = CreateSurface(scheduler);
        CardSurfaceItem notes = surface.Items.Single(item =>
            string.Equals(
                item.InstanceId,
                BuiltInCardCatalog.NotesInstanceId,
                StringComparison.Ordinal));

        notes.SelectTokenUsagePage(TokenUsageContract.CodexVendorId);

        Assert.IsNull(notes.TokenUsageCurrentPage);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-083 [USE-014] A broker snapshot reaches every card type on a full board")]
    public void BrokerSnapshotsApplyToEveryBrokerBackedCard()
    {
        using var scheduler = new CardRuntimeVisibilityScheduler();
        using CardLayoutSurfaceViewModel surface = CreateSurface(scheduler);

        // The three payload shapes the broker really publishes. Each card applies all three
        // projections to whichever one it receives, so this is where a projection that chokes
        // on a foreign payload would surface.
        AssertApplies(surface, BuiltInCardCatalog.SystemMonitorInstanceId, SystemMonitorPayload());
        AssertApplies(surface, BuiltInCardCatalog.WeatherInstanceId, WeatherPayload());
        AssertApplies(surface, BuiltInCardCatalog.TokenUsageInstanceId, TokenUsagePayload());
    }

    private static void AssertApplies(
        CardLayoutSurfaceViewModel surface,
        string instanceId,
        JsonElement payload)
    {
        CardSurfaceItem item = surface.Items.Single(card =>
            string.Equals(card.InstanceId, instanceId, StringComparison.Ordinal));

        item.Runtime.ApplySnapshot(
            new CardRuntimeSnapshot(
                instanceId,
                item.CardTypeId,
                BuiltInCardRuntimeFactory.CurrentSchemaVersion,
                sequence: 1,
                SampledAt,
                CardRuntimeFreshness.Fresh,
                CardRuntimeStatus.Ready,
                payload));

        Assert.AreEqual(CardRuntimeStatus.Ready, item.RuntimeStatus, instanceId);
        Assert.IsTrue(item.RuntimePresentation.IsContentVisible, instanceId);
    }

    private static JsonElement SystemMonitorPayload() =>
        JsonSerializer.SerializeToElement(
            new SystemMonitorCardPayloadDto
            {
                Metrics =
                [
                    new SystemMonitorMetricDto
                    {
                        MetricId = SystemMonitorContract.CpuUsage,
                        IconId = "cpu",
                        Status = SystemMonitorMetricStatus.Ready,
                        PrimaryText = "12%",
                        Ratio = 0.12d,
                    },
                ],
                SampledAtUtc = SampledAt.ToString("O"),
            },
            ContractJson.Options);

    private static JsonElement WeatherPayload() =>
        JsonSerializer.SerializeToElement(
            new
            {
                locationLabel = "Singapore",
                temperatureText = "31 °C",
                conditionCode = 0,
                observedAtUtc = SampledAt.ToString("O"),
            },
            ContractJson.Options);

    private static JsonElement TokenUsagePayload() =>
        JsonSerializer.SerializeToElement(
            new TokenUsageCardPayloadDto
            {
                Pages =
                [
                    new TokenUsagePageDto
                    {
                        PageId = TokenUsageContract.OverviewPageId,
                        Metrics =
                        [
                            new TokenUsageMetricDto
                            {
                                MetricId = TokenUsageContract.TodayBilledTokens,
                                Status = TokenUsageMetricStatus.Ready,
                                PrimaryText = "735K",
                            },
                        ],
                    },
                ],
                SampledAtUtc = SampledAt.ToString("O"),
            },
            ContractJson.Options);

    private static CardLayoutSurfaceViewModel CreateSurface(
        CardRuntimeVisibilityScheduler scheduler)
    {
        var layout = new CardLayoutViewModel(
            4,
            BuiltInCardCatalog.Addable
                .Select(card => new CardLayoutItem(
                    card.InstanceId,
                    card.Definition.DefaultSize))
                .ToArray());

        return new CardLayoutSurfaceViewModel(
            new CardLayoutEditViewModel(layout),
            new NoteEditorViewModel(null),
            new NoteSearchViewModel(null),
            status => status.ToString(),
            scheduler);
    }
}
