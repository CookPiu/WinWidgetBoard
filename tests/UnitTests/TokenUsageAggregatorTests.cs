using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The arithmetic behind the card. Two of these are regressions for defects that produced
/// plausible-looking wrong numbers rather than errors: the trend dropping the current hour, and
/// a peak rate averaged over a different window length than the current rate it sits beside.
/// </summary>
[TestClass]
public sealed class TokenUsageAggregatorTests
{
    // Fixed offset, so "today" and the hour buckets are the same on any machine that runs this.
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-TOKUSE-020 [USE-003] The same response written once per content block counts once")]
    public void DeduplicatesRepeatedResponseLines()
    {
        var aggregator = new TokenUsageAggregator(Utc);

        // This is what the real transcripts look like: one response, four lines, each carrying
        // a full copy of the usage. Summing them would nearly double the day's total.
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 5, billed: 100),
                Record("req_1", "msg_1", minutesAgo: 5, billed: 100),
                Record("req_1", "msg_1", minutesAgo: 5, billed: 100),
                Record("req_1", "msg_1", minutesAgo: 5, billed: 100),
            ],
            Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);
        Assert.AreEqual(1, aggregate.TodayRequests);
        Assert.AreEqual(100L, aggregate.TodayBilledTokens);
        Assert.AreEqual(3L, aggregator.DuplicateCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-021 [USE-003] A retried request with the same message id counts twice")]
    public void KeepsRetriesApart()
    {
        var aggregator = new TokenUsageAggregator(Utc);

        // Same message id, different request: two calls that were both billed.
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 5, billed: 100),
                Record("req_2", "msg_1", minutesAgo: 4, billed: 100),
            ],
            Now);

        Assert.AreEqual(2, aggregator.Compute(Now).TodayRequests);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-022 [USE-004] The trend's last bucket is the current hour")]
    public void TrendIncludesTheCurrentHour()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 6, billed: 500)], Now);

        IReadOnlyList<TokenUsageHourBucket> trend = aggregator.Compute(Now).Trend;

        Assert.AreEqual(TokenUsageContract.TrendHours, trend.Count);
        // Regression: bucketing by the distance from the start of the current hour floors to
        // -1 for anything inside it, which dropped the most recent readings entirely.
        Assert.AreEqual(500L, trend[^1].BilledTokens);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero), trend[^1].HourStartLocal);
        Assert.AreEqual(0L, trend[^2].BilledTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-023 [USE-004] An earlier hour lands in its own bucket")]
    public void TrendPlacesAnEarlierHourCorrectly()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        // 07:46, three hours before the 10:00 bucket.
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 180, billed: 300)], Now);

        IReadOnlyList<TokenUsageHourBucket> trend = aggregator.Compute(Now).Trend;

        Assert.AreEqual(300L, trend[^4].BilledTokens);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero), trend[^4].HourStartLocal);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-024 [USE-005] The peak rate is measured over the same window as the current rate")]
    public void PeakRateUsesTheRateWindowLength()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        // One burst, entirely inside a single rate window an hour ago.
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 65, billed: 15_000)], Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);

        // Regression: averaging the peak over an hour while the current rate is averaged over
        // fifteen minutes made a quiet "now" read as four times busier than the busiest burst.
        Assert.AreEqual(
            15_000d / TokenUsageContract.RateWindowMinutes,
            aggregate.PeakRatePerMinute,
            0.001d);
        Assert.IsNotNull(aggregate.PeakWindowStartLocal);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-025 [USE-005] The current rate covers only the trailing window")]
    public void CurrentRateIgnoresOlderResponses()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 5, billed: 3_000),
                Record("req_2", "msg_2", minutesAgo: 120, billed: 999_000),
            ],
            Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);

        Assert.IsTrue(aggregate.HasCurrentRate);
        Assert.AreEqual(
            3_000d / TokenUsageContract.RateWindowMinutes,
            aggregate.CurrentRatePerMinute,
            0.001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-026 [USE-005] An idle window is a zero rate, not a missing one")]
    public void IdleWindowReportsZero()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 300, billed: 5_000)], Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);

        Assert.IsFalse(aggregate.HasCurrentRate);
        Assert.AreEqual(0d, aggregate.CurrentRatePerMinute, 0.001d);
        Assert.IsTrue(aggregate.HasAnyRecord);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-027 [USE-006] The cache hit rate is reads over all cacheable input")]
    public void CacheHitRateUsesCacheableInput()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                new TranscriptUsageRecord(
                    "req_1",
                    "msg_1",
                    "claude-opus-5",
                    Now.AddMinutes(-5),
                    InputTokens: 10,
                    OutputTokens: 1_000,
                    CacheCreationTokens: 10,
                    CacheReadTokens: 80),
            ],
            Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);

        // Output is not input and must not dilute the rate; cache creation counts, because
        // those tokens were sent uncached this time.
        Assert.AreEqual(0.8d, aggregate.CacheHitRate!.Value, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-028 [USE-003] Records older than the retention window are pruned")]
    public void PrunesRecordsOutsideTheWindow()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 5, billed: 100)], Now);
        Assert.AreEqual(1, aggregator.RecordCount);

        // Thirty hours later - past the retention window, which is deliberately longer than a
        // day - the earlier record must not survive, or the dedup set becomes the one
        // structure that grows for as long as the broker runs.
        aggregator.Ingest(
            [Record("req_2", "msg_2", minutesAgo: -1_800, billed: 100)],
            Now.AddHours(30));

        Assert.AreEqual(1, aggregator.RecordCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-029 [USE-003] Yesterday's responses stay out of today's totals")]
    public void TodayExcludesYesterday()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                // 23:46 the previous day - inside retention, outside today.
                Record("req_1", "msg_1", minutesAgo: 660, billed: 7_000),
                Record("req_2", "msg_2", minutesAgo: 5, billed: 300),
            ],
            Now);

        TokenUsageAggregate aggregate = aggregator.Compute(Now);

        Assert.AreEqual(1, aggregate.TodayRequests);
        Assert.AreEqual(300L, aggregate.TodayBilledTokens);
        // The trend spans the retained window, so it still sees the earlier response.
        Assert.AreEqual(7_300L, aggregate.Trend.Sum(bucket => bucket.BilledTokens));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-030 [USE-006] Models are ranked by billed tokens and capped")]
    public void RanksModelsByUsage()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        var records = new List<TranscriptUsageRecord>();
        for (int i = 0; i < TokenUsageContract.MaxModelRows + 3; i++)
        {
            records.Add(
                Record($"req_{i}", $"msg_{i}", minutesAgo: 5, billed: 100 * (i + 1))
                    with { Model = $"model-{i}" });
        }

        aggregator.Ingest(records, Now);
        IReadOnlyList<TokenUsageModelTotal> models = aggregator.Compute(Now).Models;

        Assert.AreEqual(TokenUsageContract.MaxModelRows, models.Count);
        Assert.AreEqual("model-7", models[0].Model);
        Assert.IsTrue(models[0].BilledTokens > models[^1].BilledTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-031 [USE-003] No records at all is an empty aggregate")]
    public void EmptyAggregateWhenNothingIngested()
    {
        TokenUsageAggregate aggregate = new TokenUsageAggregator(Utc).Compute(Now);

        Assert.IsFalse(aggregate.HasAnyRecord);
        Assert.AreEqual(0, aggregate.TodayRequests);
        Assert.IsNull(aggregate.CacheHitRate);
        Assert.AreEqual(0, aggregate.Trend.Count);
    }

    private static TranscriptUsageRecord Record(
        string requestId,
        string messageId,
        int minutesAgo,
        long billed) =>
        new(
            requestId,
            messageId,
            "claude-opus-5",
            Now.AddMinutes(-minutesAgo),
            InputTokens: 0,
            OutputTokens: billed,
            CacheCreationTokens: 0,
            CacheReadTokens: 0);
}
