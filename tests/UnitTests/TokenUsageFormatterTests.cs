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
        "UT-TOKUSE-121 [USE-004] The spend curve spans the elapsed day and peaks at the busiest hour")]
    public void SpendCurveSpansTheElapsedDay()
    {
        TokenUsageAggregate aggregate = ReadyAggregate() with
        {
            TodayHours =
            [
                Bucket(0, hour: 0, cost: 0m),
                Bucket(50_000, hour: 1, cost: 1.00m),
                Bucket(200_000, hour: 2, cost: 3.00m),
            ],
            TodayElapsedHours = 2.5d,
            TodayCostUsd = 4.00m,
        };

        TokenUsageSpendPointDto[] points = TokenUsageFormatter.FormatSpendCurve(aggregate);

        Assert.AreEqual(3, points.Length);
        // Each point closes its hour; the axis is the two and a half hours that have passed,
        // so the first hour ends at 0.4 and the open hour at exactly 1. Heights are each
        // hour's own spend against the busiest hour, which therefore touches the top.
        Assert.AreEqual(0.4d, points[0].Fraction, 0.0001d);
        Assert.AreEqual(0d, points[0].Level, 0.0001d);
        Assert.AreEqual("\u2248$0.00", points[0].CostText);
        Assert.AreEqual(0.8d, points[1].Fraction, 0.0001d);
        Assert.AreEqual(1d / 3d, points[1].Level, 0.0001d);
        Assert.AreEqual("\u2248$1.00", points[1].CostText);
        Assert.AreEqual("50.0K", points[1].BilledText);
        Assert.AreEqual(1d, points[2].Fraction, 0.0001d);
        Assert.AreEqual(1d, points[2].Level, 0.0001d);
        Assert.AreEqual("\u2248$3.00", points[2].CostText);
        Assert.AreEqual(2, points[2].Hour);
        Assert.IsTrue(points[2].IsCurrent);
        Assert.IsFalse(points[1].IsCurrent);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-122 [USE-004] An unpriced day draws its curve from tokens and says no amount")]
    public void SpendCurveFallsBackToTokensWhenUnpriced()
    {
        TokenUsageAggregate aggregate = ReadyAggregate() with
        {
            TodayHours = [Bucket(100_000, hour: 0), Bucket(300_000, hour: 1)],
            TodayElapsedHours = 1.5d,
            TodayCostUsd = 0m,
            UnpricedModelCount = 1,
        };

        TokenUsageSpendPointDto[] points = TokenUsageFormatter.FormatSpendCurve(aggregate);

        // The headline falls back to a note in this case; a flat curve under it would read as
        // a fault rather than as "nothing could be priced".
        Assert.AreEqual(1d / 3d, points[0].Level, 0.0001d);
        Assert.AreEqual(1d, points[1].Level, 0.0001d);
        Assert.AreEqual(string.Empty, points[0].CostText);
        Assert.AreEqual(string.Empty, points[1].CostText);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-123 [USE-007] The tiles' figures and each slice's amount are composed by the broker")]
    public void ComposesTileFiguresAndSliceAmounts()
    {
        TokenUsagePageDto payload = Page(ReadyAggregate());

        // Billed plus cache reads, and billed per response: the two sums a reader would
        // otherwise do in their head.
        Assert.AreEqual("11.4M", payload.TotalTokensText);
        Assert.AreEqual("5,479", payload.AverageBilledPerRequestText);
        Assert.AreEqual("\u2248$12.34", payload.Breakdown[0].CostText);
        Assert.AreEqual(2, payload.SpendCurve.Count);

        TokenUsagePageDto empty = Page(new TokenUsageAggregate());
        Assert.AreEqual(string.Empty, empty.TotalTokensText);
        Assert.AreEqual(string.Empty, empty.AverageBilledPerRequestText);
        Assert.AreEqual(0, empty.SpendCurve.Count);
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

    private static TokenUsageHourBucket Bucket(long billed, int hour, decimal cost = 0m) =>
        new(
            new DateTimeOffset(2026, 8, 28, hour, 0, 0, TimeSpan.Zero),
            billed,
            Requests: 1,
            cost);

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
            TodayHours = [Bucket(1, hour: 9), Bucket(2, hour: 10)],
            TodayElapsedHours = 10d + 46d / 60d,
            Breakdown = [new TokenUsageSlice("claude-opus-5", 300_000, 73, 12.34m)],
            CacheHitRate = 0.75d,
            TodayCostUsd = 12.34m,
        };
}
