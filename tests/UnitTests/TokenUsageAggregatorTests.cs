using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The arithmetic behind the card. Two of these are regressions for defects that produced
/// plausible-looking wrong numbers rather than errors: the hourly buckets dropping the current
/// hour, and a peak rate averaged over a different window length than the current rate it sits
/// beside.
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

        TokenUsageAggregate aggregate = Overview(aggregator, Now);
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

        Assert.AreEqual(2, Overview(aggregator, Now).TodayRequests);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-022 [USE-004] Today's hours run from midnight to the current hour, which is last")]
    public void TodayHoursIncludeTheCurrentHour()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 6, billed: 500)], Now);

        TokenUsageAggregate aggregate = Overview(aggregator, Now);
        IReadOnlyList<TokenUsageHourBucket> hours = aggregate.TodayHours;

        // 10:46: eleven buckets, 00:00 through 10:00, the last one still open.
        Assert.AreEqual(11, hours.Count);
        // Regression: bucketing by the distance from the start of the current hour floors to
        // -1 for anything inside it, which dropped the most recent readings entirely.
        Assert.AreEqual(500L, hours[^1].BilledTokens);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero), hours[^1].HourStartLocal);
        Assert.AreEqual(0L, hours[^2].BilledTokens);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero), hours[0].HourStartLocal);
        Assert.AreEqual(10d + 46d / 60d, aggregate.TodayElapsedHours, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-023 [USE-004] An earlier hour lands in its own bucket")]
    public void TodayHoursPlaceAnEarlierHourCorrectly()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        // 07:46, three hours before the 10:00 bucket.
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 180, billed: 300)], Now);

        IReadOnlyList<TokenUsageHourBucket> hours = Overview(aggregator, Now).TodayHours;

        Assert.AreEqual(300L, hours[7].BilledTokens);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero), hours[7].HourStartLocal);
        Assert.AreEqual(1, hours[7].Requests);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-120 [USE-004] The hours' costs add up to the day's cost, and so do the slices'")]
    public void HourAndSliceCostsAddUpToTheDay()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 180, billed: 300_000),
                Record("req_2", "msg_2", minutesAgo: 6, billed: 100_000),
            ],
            Now);

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

        // Priced by the built-in table; the point is that each hour and each slice carries
        // its own share on the same terms as the total, so the curve and the split meter
        // never disagree with the headline they sit under.
        Assert.IsTrue(aggregate.TodayCostUsd > 0m);
        Assert.AreEqual(aggregate.TodayCostUsd, aggregate.TodayHours.Sum(hour => hour.CostUsd));
        Assert.AreEqual(aggregate.TodayCostUsd, aggregate.Breakdown.Sum(slice => slice.CostUsd));
        Assert.IsTrue(aggregate.TodayHours[7].CostUsd > aggregate.TodayHours[10].CostUsd);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-024 [USE-005] The peak rate is measured over the same window as the current rate")]
    public void PeakRateUsesTheRateWindowLength()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        // One burst, entirely inside a single rate window an hour ago - and inside today,
        // which is the window the peak is now measured over.
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 65, billed: 15_000)], Now);

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

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

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

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

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

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
                    TokenUsageContract.ClaudeVendorId,
                    "req_1",
                    "msg_1",
                    "claude-opus-5",
                    Now.AddMinutes(-5),
                    InputTokens: 10,
                    OutputTokens: 1_000,
                    CacheWrite5mTokens: 10,
                    CacheWrite1hTokens: 0,
                    CacheReadTokens: 80),
            ],
            Now);

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

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

        TokenUsageAggregate aggregate = Overview(aggregator, Now);

        Assert.AreEqual(1, aggregate.TodayRequests);
        Assert.AreEqual(300L, aggregate.TodayBilledTokens);
        // The hourly buckets are today's too, so the earlier response is in none of them.
        Assert.AreEqual(300L, aggregate.TodayHours.Sum(bucket => bucket.BilledTokens));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-030 [USE-006] A vendor page ranks its models by billed tokens and caps them")]
    public void RanksModelsByUsage()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        var records = new List<TranscriptUsageRecord>();
        for (int i = 0; i < TokenUsageContract.MaxBreakdownRows + 3; i++)
        {
            records.Add(
                Record($"req_{i}", $"msg_{i}", minutesAgo: 5, billed: 100 * (i + 1))
                    with { Model = $"model-{i}" });
        }

        aggregator.Ingest(records, Now);
        IReadOnlyList<TokenUsageSlice> models = aggregator
            .Compute(Now, TokenUsageContract.VendorIds)
            .Vendors
            .Single(vendor => vendor.VendorId == TokenUsageContract.ClaudeVendorId)
            .Aggregate
            .Breakdown;

        Assert.AreEqual(TokenUsageContract.MaxBreakdownRows, models.Count);
        Assert.AreEqual("model-7", models[0].Label);
        Assert.IsTrue(models[0].BilledTokens > models[^1].BilledTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-032 [USE-012] The overview splits by vendor, a vendor page by model")]
    public void OverviewSplitsByVendorAndVendorPageByModel()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 5, billed: 300),
                Record("req_2", "msg_2", minutesAgo: 5, billed: 700)
                    with { VendorId = TokenUsageContract.CodexVendorId, Model = "gpt-5.6-sol" },
            ],
            Now);

        TokenUsageReport report = aggregator.Compute(Now, TokenUsageContract.VendorIds);

        // The two vendors are not interchangeable, so the overview names them rather than
        // merging their model lists into one ranking that hides which tool spent what.
        CollectionAssert.AreEquivalent(
            new[] { TokenUsageContract.ClaudeVendorId, TokenUsageContract.CodexVendorId },
            report.Overview.Breakdown.Select(slice => slice.Label).ToArray());
        Assert.AreEqual(1_000L, report.Overview.TodayBilledTokens);
        Assert.AreEqual(
            "gpt-5.6-sol",
            report.Vendors
                .Single(vendor => vendor.VendorId == TokenUsageContract.CodexVendorId)
                .Aggregate.Breakdown.Single().Label);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-033 [USE-012] A disabled vendor leaves the totals immediately")]
    public void DisabledVendorIsExcluded()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest(
            [
                Record("req_1", "msg_1", minutesAgo: 5, billed: 300),
                Record("req_2", "msg_2", minutesAgo: 5, billed: 700)
                    with { VendorId = TokenUsageContract.CodexVendorId },
            ],
            Now);

        TokenUsageReport report = aggregator.Compute(
            Now,
            [TokenUsageContract.ClaudeVendorId]);

        Assert.AreEqual(300L, report.Overview.TodayBilledTokens);
        Assert.AreEqual(1, report.Vendors.Count);

        // Forgetting drops the records outright rather than waiting for them to age out, so a
        // vendor switched off stops counting on the next tick instead of a day later.
        aggregator.Forget(TokenUsageContract.CodexVendorId);
        Assert.AreEqual(1, aggregator.RecordCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-034 [USE-012] An enabled vendor with no records still gets a page")]
    public void EnabledVendorWithoutRecordsStillGetsAPage()
    {
        var aggregator = new TokenUsageAggregator(Utc);
        aggregator.Ingest([Record("req_1", "msg_1", minutesAgo: 5, billed: 300)], Now);

        TokenUsageReport report = aggregator.Compute(Now, TokenUsageContract.VendorIds);

        // "Switched on and did nothing today" is a different statement from the page being
        // absent, and the card has to be able to say it.
        TokenUsageVendorReport codex = report.Vendors.Single(vendor =>
            vendor.VendorId == TokenUsageContract.CodexVendorId);
        Assert.IsFalse(codex.Aggregate.HasAnyRecord);
        Assert.AreEqual(0, codex.Aggregate.TodayRequests);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-031 [USE-003] No records at all is an empty aggregate")]
    public void EmptyAggregateWhenNothingIngested()
    {
        TokenUsageAggregate aggregate = Overview(new TokenUsageAggregator(Utc), Now);

        Assert.IsFalse(aggregate.HasAnyRecord);
        Assert.AreEqual(0, aggregate.TodayRequests);
        Assert.IsNull(aggregate.CacheHitRate);
        Assert.AreEqual(0, aggregate.TodayHours.Count);
    }

    /// <summary>
    /// The overview page, which is what every assertion here is about. Vendor pages get their
    /// own coverage where they differ.
    /// </summary>
    private static TokenUsageAggregate Overview(
        TokenUsageAggregator aggregator,
        DateTimeOffset nowUtc) =>
        aggregator.Compute(nowUtc, TokenUsageContract.VendorIds).Overview;

    private static TranscriptUsageRecord Record(
        string requestId,
        string messageId,
        int minutesAgo,
        long billed) =>
        new(
            TokenUsageContract.ClaudeVendorId,
            requestId,
            messageId,
            "claude-opus-5",
            Now.AddMinutes(-minutesAgo),
            InputTokens: 0,
            OutputTokens: billed,
            CacheWrite5mTokens: 0,
            CacheWrite1hTokens: 0,
            CacheReadTokens: 0);
}
