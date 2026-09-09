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

    /// <summary>Both vendors reporting: the fullest page the card has to lay out.</summary>
    internal static JsonElement ReadyWithQuota() =>
        JsonSerializer.SerializeToElement(
            TokenUsageFormatter.CreatePayload(
                new TokenUsageReport(
                    Aggregate(),
                    [
                        new TokenUsageVendorReport(
                            TokenUsageContract.ClaudeVendorId,
                            Aggregate()),
                        new TokenUsageVendorReport(
                            TokenUsageContract.CodexVendorId,
                            Aggregate()),
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
            TodayHours = TodayHours(),
            TodayElapsedHours = 10d + 46d / 60d,
            Breakdown = [new TokenUsageSlice("claude-opus-5", 1_580_000, 279, 108.80m)],
            CacheHitRate = 0.988d,
            // Costed the way the broker does: the day's total, plus the per-kind shares the
            // rows carry. Output is part of the billed share, not an addition to it.
            TodayCostUsd = 108.80m,
            TodayBilledCostUsd = 26.00m,
            TodayCacheReadCostUsd = 82.80m,
            TodayOutputCostUsd = 12.10m,
        };

    private static TokenUsageHourBucket[] TodayHours()
    {
        // 00:00 through 10:00 on the sample day, with the day's usage in the open hour.
        var buckets = new TokenUsageHourBucket[11];
        for (int hour = 0; hour < buckets.Length; hour++)
        {
            bool last = hour == buckets.Length - 1;
            buckets[hour] = new TokenUsageHourBucket(
                new DateTimeOffset(2026, 8, 28, hour, 0, 0, TimeSpan.Zero),
                last ? 1_580_000 : 0,
                Requests: last ? 279 : 0,
                last ? 108.80m : 0m);
        }

        return buckets;
    }
}
