using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The money figure.
///
/// A cost is believed far more readily than a token count, so the properties pinned here are
/// the ones that decide whether it can be trusted: that an unknown model is excluded and
/// counted rather than priced at zero, that the two cache-write lifetimes are charged at their
/// different rates, and that a routed model id is not matched by resemblance to a real one.
/// </summary>
[TestClass]
public sealed class TokenUsagePricingTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-TOKUSE-100 [USE-016] A response is costed from the published per-kind rates")]
    public void CostsAResponseFromPublishedRates()
    {
        // Claude Opus 5: $5 input, $25 output, $6.25 5m cache write, $10 1h cache write,
        // $0.50 cache read - all per million tokens.
        decimal? cost = TokenUsagePricing.TryGetCost(
            new TranscriptUsageRecord(
                TokenUsageContract.ClaudeVendorId,
                "req_1",
                "msg_1",
                "claude-opus-5",
                Now,
                InputTokens: 1_000_000,
                OutputTokens: 1_000_000,
                CacheWrite5mTokens: 1_000_000,
                CacheWrite1hTokens: 1_000_000,
                CacheReadTokens: 1_000_000));

        Assert.AreEqual(46.75m, cost);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-101 [USE-016] The two cache-write lifetimes are charged differently")]
    public void CacheWriteLifetimesArePricedApart()
    {
        decimal fiveMinute = TokenUsagePricing.TryGetCost(Record(cacheWrite5m: 1_000_000))!.Value;
        decimal oneHour = TokenUsagePricing.TryGetCost(Record(cacheWrite1h: 1_000_000))!.Value;

        // 1.25x versus 2x the base input rate - a 60% difference on what is usually the
        // largest billed component, which is why the scanner reads the split rather than
        // charging the flat total at one rate.
        Assert.AreEqual(6.25m, fiveMinute);
        Assert.AreEqual(10m, oneHour);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-102 [USE-016] A model with no published rate has no cost, not a zero one")]
    public void UnknownModelHasNoCost()
    {
        Assert.IsNull(TokenUsagePricing.TryGetRate("deepseek-v4-flash"));
        Assert.IsNull(TokenUsagePricing.TryGetCost(Record(model: "deepseek-v4-flash")));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-103 [USE-016] A routed model id is not priced by resemblance")]
    public void RoutedModelIdIsNotMatchedByResemblance()
    {
        // It contains "claude-opus-5", but whether a proxy bills at the first-party rate is
        // not something this build knows. Guessing would put a confident number on it.
        Assert.IsNull(TokenUsagePricing.TryGetRate("anthropic/claude-opus-5-ps-aws-dst"));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-104 [USE-016] Unpriced usage is excluded from the total and counted")]
    public void UnpricedUsageIsExcludedAndCounted()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                Record(model: "claude-opus-5", output: 1_000_000) with
                {
                    RequestId = "req_1",
                    MessageId = "msg_1",
                },
                Record(model: "deepseek-v4-flash", output: 1_000_000) with
                {
                    RequestId = "req_2",
                    MessageId = "msg_2",
                },
            ],
            Now);

        TokenUsageAggregate overview = aggregator
            .Compute(Now, TokenUsageContract.VendorIds)
            .Overview;

        // Both responses count as usage; only the priced one contributes money.
        Assert.AreEqual(2, overview.TodayRequests);
        Assert.AreEqual(25m, overview.TodayCostUsd);
        Assert.AreEqual(1, overview.UnpricedModelCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-105 [USE-016] A partial total is marked as a floor, not a total")]
    public void PartialTotalIsMarkedAsAFloor()
    {
        string complete = TokenUsageFormatter.FormatCost(
            new TokenUsageAggregate { TodayCostUsd = 12.3456m });
        string partial = TokenUsageFormatter.FormatCost(
            new TokenUsageAggregate { TodayCostUsd = 12.3456m, UnpricedModelCount = 1 });

        Assert.AreEqual("≈$12.35", complete);
        // The trailing plus says "at least this much" - without it the figure would quietly
        // omit whatever could not be priced.
        Assert.AreEqual("≈$12.35+", partial);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-106 [USE-016] Nothing priced means no amount at all")]
    public void NothingPricedShowsNoAmount()
    {
        Assert.AreEqual(
            string.Empty,
            TokenUsageFormatter.FormatCost(new TokenUsageAggregate { UnpricedModelCount = 2 }));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-107 [USE-016] The cost rides on the billed reading and the page")]
    public void CostReachesTheCardOnTheBilledReading()
    {
        var aggregate = new TokenUsageAggregate
        {
            HasAnyRecord = true,
            TodayBilledTokens = 1_000_000,
            TodayCacheReadTokens = 40_000_000,
            TodayRequests = 3,
            TodayCostUsd = 25m,
            TodayBilledCostUsd = 5m,
            TodayCacheReadCostUsd = 20m,
        };

        TokenUsagePageDto page = TokenUsageFormatter.CreatePage(
            TokenUsageContract.OverviewPageId,
            aggregate,
            Utc,
            Now);

        // The page carries the day's total, which the card shows as its headline.
        Assert.AreEqual("≈$25.00", page.CostText);
        Assert.IsTrue(page.HasCost);

        // Each row carries its own share, so the rows add up instead of each repeating the
        // total. Cache reads are usually the larger of the two by far.
        Assert.AreEqual(
            "≈$5.00",
            page.Metrics.Single(m => m.MetricId == TokenUsageContract.TodayBilledTokens)
                .SecondaryText);
        Assert.AreEqual(
            "≈$20.00",
            page.Metrics.Single(m => m.MetricId == TokenUsageContract.TodayCacheReadTokens)
                .SecondaryText);
    }

    private static TranscriptUsageRecord Record(
        string model = "claude-opus-5",
        long input = 0,
        long output = 0,
        long cacheWrite5m = 0,
        long cacheWrite1h = 0,
        long cacheRead = 0) =>
        new(
            TokenUsageContract.ClaudeVendorId,
            "req_1",
            "msg_1",
            model,
            Now,
            input,
            output,
            cacheWrite5m,
            cacheWrite1h,
            cacheRead);
}
