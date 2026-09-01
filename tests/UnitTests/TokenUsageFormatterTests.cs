using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The card binds these strings and never sees the numbers behind them, so the units, the
/// rounding and the empty states are pinned here. Everything the formatter emits has to be
/// language-neutral - the metric names are the only translated part, and they live in the
/// panel's resources.
/// </summary>
[TestClass]
public sealed class TokenUsageFormatterTests
{
    private static readonly DateTimeOffset SampledAt =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    private static readonly double[] IdleTrendExpectation = [0d, 0d];

    [TestMethod(DisplayName =
        "UT-TOKUSE-040 [USE-007] Token counts are exact below ten thousand and scaled above")]
    [DataRow(0L, "0")]
    [DataRow(942L, "942")]
    [DataRow(9_999L, "9,999")]
    [DataRow(10_000L, "10.0K")]
    [DataRow(15_723L, "15.7K")]
    [DataRow(987_654L, "988K")]
    [DataRow(1_486_175_152L, "1.49B")]
    public void FormatsTokenCounts(long tokens, string expected) =>
        Assert.AreEqual(expected, TokenUsageFormatter.FormatTokenCount(tokens));

    [TestMethod(DisplayName =
        "UT-TOKUSE-041 [USE-007] A rate carries its own unit")]
    public void FormatsRate() =>
        Assert.AreEqual("15.9K/min", TokenUsageFormatter.FormatRate(15_900d));

    [TestMethod(DisplayName =
        "UT-TOKUSE-042 [USE-007] A non-finite rate degrades to the placeholder")]
    public void RejectsNonFiniteRate() =>
        Assert.AreEqual(
            TokenUsageFormatter.Placeholder,
            TokenUsageFormatter.FormatRate(double.NaN));

    [TestMethod(DisplayName =
        "UT-TOKUSE-043 [USE-004] The trend is scaled against its own peak")]
    public void NormalizesTrendAgainstItsPeak()
    {
        TokenUsageHourBucket[] trend =
        [
            Bucket(0),
            Bucket(50),
            Bucket(200),
        ];

        double[] normalized = TokenUsageFormatter.NormalizeTrend(trend);

        Assert.AreEqual(0d, normalized[0], 0.0001d);
        Assert.AreEqual(0.25d, normalized[1], 0.0001d);
        Assert.AreEqual(1d, normalized[2], 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-044 [USE-004] An idle day normalises to zeros rather than dividing by zero")]
    public void NormalizesAnIdleTrend()
    {
        double[] normalized = TokenUsageFormatter.NormalizeTrend([Bucket(0), Bucket(0)]);

        CollectionAssert.AreEqual(IdleTrendExpectation, normalized);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-045 [USE-007] An empty aggregate reports every metric as empty")]
    public void EmptyAggregateProducesEmptyMetrics()
    {
        TokenUsagePageDto payload = Page(new TokenUsageAggregate());

        Assert.AreEqual(TokenUsageContract.MetricIds.Count, payload.Metrics.Count);
        foreach (TokenUsageMetricDto metric in payload.Metrics)
        {
            Assert.AreEqual(TokenUsageMetricStatus.Empty, metric.Status);
            Assert.AreEqual(TokenUsageFormatter.Placeholder, metric.PrimaryText);
            Assert.IsTrue(TokenUsageContract.IsKnownMetricId(metric.MetricId));
        }

        Assert.AreEqual(0, payload.Breakdown.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-046 [USE-007] Only the hit rate carries a meter")]
    public void OnlyBoundedMetricsCarryARatio()
    {
        TokenUsagePageDto payload = Page(ReadyAggregate());

        // A token count and a rate have no ceiling to draw a bar against; inventing one would
        // make the card assert something the data does not say.
        Assert.IsNull(Metric(payload, TokenUsageContract.TodayBilledTokens).Ratio);
        Assert.IsNull(Metric(payload, TokenUsageContract.TodayCacheReadTokens).Ratio);
        Assert.IsNull(Metric(payload, TokenUsageContract.TodayRequests).Ratio);
        Assert.IsNull(Metric(payload, TokenUsageContract.TodayOutputTokens).Ratio);
        Assert.AreEqual(
            0.75d,
            Metric(payload, TokenUsageContract.CacheHitRate).Ratio!.Value,
            0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-048 [USE-006] Model shares are stated against today's real total")]
    public void ModelSharesUseTheRealTotal()
    {
        TokenUsagePageDto payload = Page(ReadyAggregate());

        Assert.AreEqual(1, payload.Breakdown.Count);
        Assert.AreEqual("claude-opus-5", payload.Breakdown[0].Label);
        Assert.AreEqual("75.0%", payload.Breakdown[0].SecondaryText);
    }

    private static TokenUsagePageDto Page(TokenUsageAggregate aggregate) =>
        TokenUsageFormatter.CreatePage(
            TokenUsageContract.OverviewPageId,
            aggregate,
            TimeZoneInfo.Utc,
            SampledAt);

    private static TokenUsageMetricDto Metric(
        TokenUsagePageDto payload,
        string metricId) =>
        payload.Metrics.Single(metric =>
            string.Equals(metric.MetricId, metricId, StringComparison.Ordinal));

    private static TokenUsageHourBucket Bucket(long billed) =>
        new(SampledAt, billed, Requests: 1);

    private static TokenUsageAggregate ReadyAggregate() =>
        new()
        {
            HasAnyRecord = true,
            TodayBilledTokens = 400_000,
            TodayOutputTokens = 96_000,
            TodayCacheReadTokens = 11_000_000,
            TodayCacheableInputTokens = 1_000,
            TodayRequests = 73,
            CurrentRatePerMinute = 15_900d,
            HasCurrentRate = true,
            PeakRatePerMinute = 14_900d,
            PeakWindowStartLocal = new DateTimeOffset(2026, 8, 28, 9, 30, 0, TimeSpan.Zero),
            Trend = [Bucket(1), Bucket(2)],
            Breakdown = [new TokenUsageSlice("claude-opus-5", 300_000, 73)],
            CacheHitRate = 0.75d,
        };
}
