using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// Card payloads built through the broker's own formatter rather than hand-written JSON, so a
/// test fixture cannot drift from the shape the panel really receives.
/// </summary>
internal static class TokenUsageCardPayloads
{
    private static readonly DateTimeOffset SampledAt =
        new(2026, 8, 28, 10, 46, 0, TimeSpan.Zero);

    /// <summary>
    /// Both vendors reporting, with Codex carrying a quota window that has not reset yet -
    /// the fullest page the card ever has to lay out.
    /// </summary>
    internal static JsonElement ReadyWithQuota() =>
        JsonSerializer.SerializeToElement(
            TokenUsageFormatter.CreatePayload(
                new TokenUsageReport(
                    Aggregate(),
                    [
                        new TokenUsageVendorReport(
                            TokenUsageContract.ClaudeVendorId,
                            Aggregate(),
                            null),
                        new TokenUsageVendorReport(
                            TokenUsageContract.CodexVendorId,
                            Aggregate(),
                            new VendorQuotaSnapshot(
                                [
                                    new VendorQuotaWindow(
                                        "secondary",
                                        31d,
                                        10_080,
                                        SampledAt.AddDays(5)),
                                ],
                                "2927.96",
                                SampledAt.AddMinutes(-30))),
                    ]),
                SampledAt,
                TimeZoneInfo.Utc),
            ContractJson.Options);

    private static TokenUsageAggregate Aggregate() =>
        new()
        {
            HasAnyRecord = true,
            TodayBilledTokens = 1_580_000,
            TodayOutputTokens = 357_000,
            TodayCacheReadTokens = 97_800_000,
            TodayCacheableInputTokens = 99_000_000,
            TodayRequests = 279,
            CurrentRatePerMinute = 4_147d,
            HasCurrentRate = true,
            PeakRatePerMinute = 39_300d,
            PeakWindowStartLocal = new DateTimeOffset(2026, 8, 28, 13, 45, 0, TimeSpan.Zero),
            Trend = Trend(),
            Breakdown = [new TokenUsageSlice("claude-opus-5", 1_580_000, 279)],
            CacheHitRate = 0.988d,
        };

    private static TokenUsageHourBucket[] Trend()
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
