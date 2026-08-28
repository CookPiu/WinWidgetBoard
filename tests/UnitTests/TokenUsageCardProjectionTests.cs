using System.Collections.ObjectModel;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// What the card makes of a broker payload. The rules pinned here are the ones that decide
/// whether the card asserts something the data does not say: a meter only where a reading has a
/// real ceiling, a reading this build cannot name dropped rather than shown unlabelled, and a
/// vendor's quota block shown only where that vendor actually reported one.
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
        TokenUsagePage page = Overview(Project(ReadyReport()));

        Assert.AreEqual(TokenUsageContract.MetricIds.Count, page.Metrics.Count);
        Assert.IsTrue(page.HasData);
        TokenUsageMetricRow billed = Row(page, TokenUsageContract.TodayBilledTokens);
        // The broker never sends a name - that is the one part which has to be translated.
        Assert.AreEqual("name:TokenUsageMetric.TodayBilled", billed.Name);
        Assert.IsTrue(billed.HasReading);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-061 [USE-008] Only a reading with a ceiling shows a meter")]
    public void MeterOnlyWhereARatioExists()
    {
        TokenUsagePage page = Overview(Project(ReadyReport()));

        Assert.IsFalse(Row(page, TokenUsageContract.TodayBilledTokens).IsMeterVisible);
        Assert.IsFalse(Row(page, TokenUsageContract.TodayCacheReadTokens).IsMeterVisible);
        Assert.IsTrue(Row(page, TokenUsageContract.CacheHitRate).IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-062 [USE-008] An unknown metric id is dropped, not shown unlabelled")]
    public void DropsUnknownMetrics()
    {
        var payload = new TokenUsageCardPayloadDto
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
                            MetricId = "usage.invented.by.a.newer.broker",
                            Status = TokenUsageMetricStatus.Ready,
                            PrimaryText = "42",
                        },
                    ],
                },
            ],
        };

        Assert.AreEqual(0, Overview(ProjectPayload(payload)).Metrics.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-063 [USE-008] An empty reading shows its localized status, not a number")]
    public void EmptyMetricShowsStatusText()
    {
        TokenUsagePage page = Overview(Project(EmptyReport()));

        Assert.IsFalse(page.HasData);
        TokenUsageMetricRow row = Row(page, TokenUsageContract.TodayBilledTokens);
        Assert.IsFalse(row.HasReading);
        Assert.IsTrue(row.IsMetricStatusTextVisible);
        Assert.AreEqual("name:TokenUsageStatus.Empty", row.MetricStatusText);
        Assert.IsFalse(row.IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-064 [USE-009] A trend bar never collapses to nothing")]
    public void TrendBarsKeepAMinimumHeight()
    {
        TokenUsagePage page = Overview(Project(ReadyReport()));

        Assert.AreEqual(TokenUsageContract.TrendHours, page.Trend.Count);
        // An hour with a little usage and an hour with none must not read the same.
        Assert.AreEqual(
            TokenUsageTrendBar.MinimumBarHeight,
            page.Trend[0].BarHeight,
            0.0001d);
        Assert.AreEqual(TokenUsageTrendBar.TrackHeight, page.Trend[^1].BarHeight, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-065 [USE-009] The trend strip is hidden when there is nothing to draw")]
    public void TrendHiddenWithoutData()
    {
        // A row of minimum-height bars reads as "zero everywhere", which is not the same as
        // having no history yet.
        Assert.IsFalse(Overview(Project(EmptyReport())).IsTrendVisible);
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
        Assert.AreEqual(0, projection.Pages.Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-067 [USE-010] Breakdown rows are capped at the contract's limit")]
    public void CapsBreakdownRows()
    {
        var payload = new TokenUsageCardPayloadDto
        {
            Pages =
            [
                new TokenUsagePageDto
                {
                    PageId = TokenUsageContract.OverviewPageId,
                    Breakdown = Enumerable
                        .Range(0, TokenUsageContract.MaxBreakdownRows + 4)
                        .Select(index => new TokenUsageBreakdownDto
                        {
                            Label = $"model-{index}",
                            PrimaryText = "1K",
                            SecondaryText = "10.0%",
                            Ratio = 0.1d,
                        })
                        .ToArray(),
                },
            ],
        };

        TokenUsagePage page = Overview(ProjectPayload(payload));

        Assert.AreEqual(TokenUsageContract.MaxBreakdownRows, page.Breakdown.Count);
        Assert.IsTrue(page.IsBreakdownVisible);
        Assert.AreEqual(10d, page.Breakdown[0].MeterPercent, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-068 [USE-008] A refresh updates rows in place instead of rebuilding them")]
    public void MergeKeepsExistingRowInstances()
    {
        ObservableCollection<TokenUsageMetricViewModel> target = [];
        TokenUsagePage first = Overview(Project(ReadyReport()));
        TokenUsageListMerger.MergeMetrics(target, first.Metrics);
        TokenUsageMetricViewModel firstRow = target[0];

        TokenUsageListMerger.MergeMetrics(target, Overview(Project(ReadyReport())).Metrics);

        // Same instances: a rebuilt source would rebuild every row's visuals on each tick.
        Assert.AreSame(firstRow, target[0]);
        Assert.AreEqual(first.Metrics.Count, target.Count);
    }

    // --- pages ------------------------------------------------------------------------------

    [TestMethod(DisplayName =
        "UT-TOKUSE-070 [USE-012] The overview comes first, then one page per enabled vendor")]
    public void ProjectsOnePagePerVendorBehindTheOverview()
    {
        TokenUsageCardProjection projection = Project(ReadyReport());

        Assert.AreEqual(3, projection.Pages.Count);
        Assert.AreEqual(TokenUsageContract.OverviewPageId, projection.Pages[0].PageId);
        Assert.AreEqual(TokenUsageContract.ClaudeVendorId, projection.Pages[1].PageId);
        Assert.AreEqual(TokenUsageContract.CodexVendorId, projection.Pages[2].PageId);
        Assert.AreEqual("name:TokenUsagePage.Overview", projection.Pages[0].Name);
        Assert.IsTrue(projection.IsPageSwitcherVisible);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-071 [USE-012] One vendor needs no switcher, because the overview repeats it")]
    public void SwitcherHiddenForASingleVendor()
    {
        var report = new TokenUsageReport(
            ReadyAggregate(),
            [new TokenUsageVendorReport(TokenUsageContract.ClaudeVendorId, ReadyAggregate())]);

        TokenUsageCardProjection projection = Project(report);

        Assert.AreEqual(2, projection.Pages.Count);
        Assert.IsFalse(projection.IsPageSwitcherVisible);
    }

    private static TokenUsagePage Overview(TokenUsageCardProjection projection) =>
        projection.Pages[0];

    private static TokenUsageMetricRow Row(TokenUsagePage page, string metricId) =>
        page.Metrics.Single(row =>
            string.Equals(row.MetricId, metricId, StringComparison.Ordinal));

    private static TokenUsageCardProjection Project(TokenUsageReport report) =>
        ProjectPayload(
            TokenUsageFormatter.CreatePayload(report, SampledAt, TimeZoneInfo.Utc));

    private static TokenUsageCardProjection ProjectPayload(TokenUsageCardPayloadDto payload) =>
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

    private static TokenUsageReport EmptyReport() =>
        new(
            new TokenUsageAggregate(),
            [
                new TokenUsageVendorReport(
                    TokenUsageContract.ClaudeVendorId,
                    new TokenUsageAggregate()),
            ]);

    private static TokenUsageReport ReadyReport() =>
        new(
            ReadyAggregate(),
            [
                new TokenUsageVendorReport(
                    TokenUsageContract.ClaudeVendorId,
                    ReadyAggregate()),
                new TokenUsageVendorReport(
                    TokenUsageContract.CodexVendorId,
                    ReadyAggregate()),
            ]);

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
            Trend = BuildTrend(),
            Breakdown = [new TokenUsageSlice("claude-opus-5", 300_000, 73)],
            CacheHitRate = 0.75d,
        };

    private static TokenUsageHourBucket[] BuildTrend()
    {
        var buckets = new TokenUsageHourBucket[TokenUsageContract.TrendHours];
        for (int i = 0; i < buckets.Length; i++)
        {
            bool last = i == buckets.Length - 1;
            buckets[i] = new TokenUsageHourBucket(
                SampledAt.AddHours(i - (TokenUsageContract.TrendHours - 1)),
                last ? 1_000 : 0,
                Requests: last ? 1 : 0);
        }

        return buckets;
    }
}
