using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// What the card makes of a broker payload. The rules pinned here are the ones that decide
/// whether the card asserts something the data does not say: a meter only where a reading has
/// a real ceiling, and a reading this build cannot name dropped rather than shown unlabelled.
/// </summary>
[TestClass]
public sealed class TokenUsageCardProjectionTests
{
    private static readonly DateTimeOffset SampledAt =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-TOKUSE-060 [USE-008] Every metric the broker sends becomes a named row")]
    public void ProjectsMetricsWithLocalizedNames()
    {
        TokenUsageCardProjection projection = Project(ReadyPayload());

        Assert.AreEqual(TokenUsageContract.MetricIds.Count, projection.Metrics.Count);
        Assert.IsTrue(projection.HasData);
        TokenUsageMetricRow billed = Row(projection, TokenUsageContract.TodayBilledTokens);
        // The broker never sends a name - that is the one part which has to be translated.
        Assert.AreEqual("name:TokenUsageMetric.TodayBilled", billed.Name);
        Assert.IsTrue(billed.HasReading);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-061 [USE-008] Only a reading with a ceiling shows a meter")]
    public void MeterOnlyWhereARatioExists()
    {
        TokenUsageCardProjection projection = Project(ReadyPayload());

        Assert.IsFalse(Row(projection, TokenUsageContract.TodayBilledTokens).IsMeterVisible);
        Assert.IsFalse(Row(projection, TokenUsageContract.CurrentRate).IsMeterVisible);
        Assert.IsTrue(Row(projection, TokenUsageContract.CacheHitRate).IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-062 [USE-008] An unknown metric id is dropped, not shown unlabelled")]
    public void DropsUnknownMetrics()
    {
        var payload = new TokenUsageCardPayloadDto
        {
            Metrics =
            [
                new TokenUsageMetricDto
                {
                    MetricId = "usage.invented.by.a.newer.broker",
                    Status = TokenUsageMetricStatus.Ready,
                    PrimaryText = "42",
                },
            ],
            SampledAtUtc = SampledAt.ToString("O"),
        };

        Assert.AreEqual(0, Project(payload).Metrics.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-063 [USE-008] An empty reading shows its localized status, not a number")]
    public void EmptyMetricShowsStatusText()
    {
        TokenUsageCardPayloadDto payload = TokenUsageFormatter.CreatePayload(
            new TokenUsageAggregate(),
            SampledAt);

        TokenUsageCardProjection projection = Project(payload);

        Assert.IsFalse(projection.HasData);
        TokenUsageMetricRow row = Row(projection, TokenUsageContract.TodayBilledTokens);
        Assert.IsFalse(row.HasReading);
        Assert.IsTrue(row.IsMetricStatusTextVisible);
        Assert.AreEqual("name:TokenUsageStatus.Empty", row.MetricStatusText);
        Assert.IsFalse(row.IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-064 [USE-009] A trend bar never collapses to nothing")]
    public void TrendBarsKeepAMinimumHeight()
    {
        TokenUsageCardProjection projection = Project(ReadyPayload());

        Assert.AreEqual(TokenUsageContract.TrendHours, projection.Trend.Count);
        // An hour with a little usage and an hour with none must not read the same.
        Assert.AreEqual(
            TokenUsageTrendBar.MinimumBarHeight,
            projection.Trend[0].BarHeight,
            0.0001d);
        Assert.AreEqual(
            TokenUsageTrendBar.TrackHeight,
            projection.Trend[^1].BarHeight,
            0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-065 [USE-009] The trend strip is hidden when there is nothing to draw")]
    public void TrendHiddenWithoutData()
    {
        TokenUsageCardPayloadDto payload = TokenUsageFormatter.CreatePayload(
            new TokenUsageAggregate(),
            SampledAt);

        // A row of minimum-height bars reads as "zero everywhere", which is not the same as
        // having no history yet.
        Assert.IsFalse(Project(payload).IsTrendVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-066 [USE-009] A malformed payload projects as empty rather than throwing")]
    public void MalformedPayloadIsEmpty()
    {
        CardRuntimeSnapshot snapshot = Snapshot(
            JsonSerializer.SerializeToElement(42, ContractJson.Options));

        TokenUsageCardProjection projection =
            TokenUsageCardProjection.FromSnapshot(snapshot, key => "name:" + key);

        Assert.IsFalse(projection.HasData);
        Assert.AreEqual(0, projection.Metrics.Count);
        Assert.AreEqual(0, projection.Trend.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-067 [USE-010] Model rows are capped at the contract's limit")]
    public void CapsModelRows()
    {
        var payload = new TokenUsageCardPayloadDto
        {
            Models = Enumerable.Range(0, TokenUsageContract.MaxModelRows + 4)
                .Select(index => new TokenUsageModelDto
                {
                    Model = $"model-{index}",
                    PrimaryText = "1K",
                    SecondaryText = "10.0%",
                    Ratio = 0.1d,
                })
                .ToArray(),
            SampledAtUtc = SampledAt.ToString("O"),
        };

        TokenUsageCardProjection projection = Project(payload);

        Assert.AreEqual(TokenUsageContract.MaxModelRows, projection.Models.Count);
        Assert.IsTrue(projection.AreModelsVisible);
        Assert.AreEqual(10d, projection.Models[0].MeterPercent, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-068 [USE-008] A refresh updates rows in place instead of rebuilding them")]
    public void MergeKeepsExistingRowInstances()
    {
        System.Collections.ObjectModel.ObservableCollection<TokenUsageMetricViewModel> target =
            [];
        TokenUsageCardProjection first = Project(ReadyPayload());
        TokenUsageListMerger.MergeMetrics(target, first.Metrics);
        TokenUsageMetricViewModel firstRow = target[0];

        TokenUsageListMerger.MergeMetrics(target, Project(ReadyPayload()).Metrics);

        // Same instances: a rebuilt source would rebuild every row's visuals on each tick.
        Assert.AreSame(firstRow, target[0]);
        Assert.AreEqual(first.Metrics.Count, target.Count);
    }

    private static TokenUsageMetricRow Row(
        TokenUsageCardProjection projection,
        string metricId) =>
        projection.Metrics.Single(row =>
            string.Equals(row.MetricId, metricId, StringComparison.Ordinal));

    private static TokenUsageCardProjection Project(TokenUsageCardPayloadDto payload) =>
        TokenUsageCardProjection.FromSnapshot(
            Snapshot(JsonSerializer.SerializeToElement(payload, ContractJson.Options)),
            key => "name:" + key);

    private static CardRuntimeSnapshot Snapshot(JsonElement payload) =>
        new(
            BuiltInCardCatalog.TokenUsageInstanceId,
            BuiltInCardCatalog.TokenUsageCardTypeId,
            BuiltInCardRuntimeFactory.CurrentSchemaVersion,
            sequence: 1,
            SampledAt,
            CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Ready,
            payload);

    private static TokenUsageCardPayloadDto ReadyPayload()
    {
        var trend = new double[TokenUsageContract.TrendHours];
        // First bar minimal, last bar at the window's peak.
        trend[0] = 0d;
        trend[^1] = 1d;

        return TokenUsageFormatter.CreatePayload(
            new TokenUsageAggregate
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
                Trend = BuildTrend(),
                Models = [new TokenUsageModelTotal("claude-opus-5", 300_000, 73)],
                CacheHitRate = 0.75d,
            },
            SampledAt);
    }

    private static TokenUsageHourBucket[] BuildTrend()
    {
        var buckets = new TokenUsageHourBucket[TokenUsageContract.TrendHours];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new TokenUsageHourBucket(
                SampledAt.AddHours(i - (TokenUsageContract.TrendHours - 1)),
                i == buckets.Length - 1 ? 1_000 : 0,
                Requests: i == buckets.Length - 1 ? 1 : 0);
        }

        return buckets;
    }
}
