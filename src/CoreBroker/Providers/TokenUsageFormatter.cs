using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Turns a report into the exact strings the card displays, and places each page's spend curve
/// on the 0..1 axes it draws against. The broker owns units and rounding here for the same reason
/// <see cref="SystemMonitorFormatter"/> does: the card should bind text rather than re-derive
/// numbers, and there is then one place where a unit is decided.
///
/// Every unit produced here is language-neutral - K, M, B, %, /min, h/d and a 24-hour clock -
/// so nothing in this file needs translating. Metric names are the part that does, and those
/// stay in the panel's own resources.
///
/// Pure: no clock, no I/O, no state. "Now" arrives as the sample instant, which is also what
/// decides whether a quota window has already reset.
/// </summary>
public static class TokenUsageFormatter
{
    /// <summary>Shown wherever a reading exists in principle but there is nothing to report.</summary>
    public const string Placeholder = "—";

    public static TokenUsageCardPayloadDto CreatePayload(
        TokenUsageReport report,
        DateTimeOffset sampledAtUtc,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;

        var pages = new List<TokenUsagePageDto>(report.Vendors.Count + 1)
        {
            CreatePage(
                TokenUsageContract.OverviewPageId,
                report.Overview,
                zone,
                sampledAtUtc),
        };

        foreach (TokenUsageVendorReport vendor in report.Vendors)
        {
            pages.Add(CreatePage(vendor.VendorId, vendor.Aggregate, zone, sampledAtUtc));
        }

        return new TokenUsageCardPayloadDto
        {
            Pages = pages,
            SampledAtUtc = sampledAtUtc.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    public static TokenUsagePageDto CreatePage(
        string pageId,
        TokenUsageAggregate aggregate,
        TimeZoneInfo? timeZone = null,
        DateTimeOffset nowUtc = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        return new TokenUsagePageDto
        {
            PageId = pageId,
            // Priority order, matching TokenUsageContract.MetricIds: a card too short for all
            // of them shows a prefix, so what the user asked to see first comes first - spend,
            // then volume. Each token kind carries its own cost, so the reader is not left
            // apportioning one lump sum across the rows. Responses come last because the
            // two-row card lifts them into a tile of their own and drops the row.
            Metrics =
            [
                FormatBilledTokens(aggregate),
                FormatOutputTokens(aggregate),
                FormatCacheReadTokens(aggregate),
                FormatCacheHitRate(aggregate),
                FormatRequests(aggregate),
            ],
            SpendCurve = FormatSpendCurve(aggregate),
            TotalTokensText = HasToday(aggregate)
                ? FormatTokenCount(aggregate.TodayBilledTokens + aggregate.TodayCacheReadTokens)
                : string.Empty,
            AverageBilledPerRequestText = HasToday(aggregate)
                ? FormatTokenCount(aggregate.TodayBilledTokens / aggregate.TodayRequests)
                : string.Empty,
            Breakdown = FormatBreakdown(aggregate),
            CostText = FormatCost(aggregate),
            UnpricedModelCount = aggregate.UnpricedModelCount,
        };
    }

    /// <summary>
    /// The day's spend as a curve: one point per hour so far, each at the end of its hour and
    /// at the running total through it. The horizontal axis is the part of the day that has
    /// elapsed, not the whole day, so the curve always fills the card and its right edge is
    /// always now; the vertical axis is the day's total, so the curve always ends at the top.
    /// Neither axis needs a ceiling the data does not have.
    ///
    /// Spend where anything was priced, billed tokens otherwise - the headline falls back the
    /// same way, and a curve of nothing under a figure of something would read as a fault.
    /// </summary>
    public static TokenUsageSpendPointDto[] FormatSpendCurve(TokenUsageAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (!HasToday(aggregate) || aggregate.TodayHours.Count == 0)
        {
            return Array.Empty<TokenUsageSpendPointDto>();
        }

        bool priced = aggregate.TodayCostUsd > 0m;
        // A minute's floor: at the stroke of midnight nothing has elapsed, and the first point
        // still needs a position.
        double elapsed = Math.Max(aggregate.TodayElapsedHours, 1d / 60d);
        decimal cumulativeCost = 0m;
        long cumulativeBilled = 0;
        var points = new TokenUsageSpendPointDto[aggregate.TodayHours.Count];
        for (int i = 0; i < points.Length; i++)
        {
            TokenUsageHourBucket bucket = aggregate.TodayHours[i];
            cumulativeCost += bucket.CostUsd;
            cumulativeBilled += bucket.BilledTokens;
            int hour = bucket.HourStartLocal.Hour;
            double level = priced
                ? (double)(cumulativeCost / aggregate.TodayCostUsd)
                : aggregate.TodayBilledTokens > 0
                    ? cumulativeBilled / (double)aggregate.TodayBilledTokens
                    : 0d;
            points[i] = new TokenUsageSpendPointDto
            {
                Hour = hour,
                Fraction = Math.Clamp(Math.Min(hour + 1, elapsed) / elapsed, 0d, 1d),
                Level = Math.Clamp(level, 0d, 1d),
                CumulativeCostText = !priced
                    ? string.Empty
                    : cumulativeCost > 0m
                        ? FormatAmount(aggregate, cumulativeCost)
                        : "\u2248$0.00",
                BilledText = FormatTokenCount(bucket.BilledTokens),
                IsCurrent = i == points.Length - 1,
            };
        }

        return points;
    }

    /// <summary>
    /// A token count at a glance: exact below ten thousand, then three significant digits with
    /// a magnitude suffix. Beyond about ten thousand the individual digits stop carrying
    /// meaning and only cost width, which the card does not have.
    /// </summary>
    public static string FormatTokenCount(long tokens)
    {
        if (tokens < 0)
        {
            return Placeholder;
        }

        if (tokens < 10_000)
        {
            return tokens.ToString("N0", CultureInfo.InvariantCulture);
        }

        return tokens switch
        {
            < 1_000_000 => Scale(tokens, 1_000d, "K"),
            < 1_000_000_000 => Scale(tokens, 1_000_000d, "M"),
            _ => Scale(tokens, 1_000_000_000d, "B"),
        };
    }

    /// <summary>Tokens per minute, in the same compact scale as a count.</summary>
    public static string FormatRate(double tokensPerMinute) =>
        double.IsFinite(tokensPerMinute) && tokensPerMinute >= 0
            ? FormatTokenCount((long)Math.Round(tokensPerMinute)) + "/min"
            : Placeholder;

    public static string FormatPercent(double ratio) =>
        double.IsFinite(ratio)
            ? Round(Math.Clamp(ratio, 0d, 1d) * 100d, 1) + "%"
            : Placeholder;

    private static TokenUsageMetricDto FormatBilledTokens(TokenUsageAggregate aggregate)
    {
        if (!HasToday(aggregate))
        {
            return Empty(TokenUsageContract.TodayBilledTokens);
        }

        return Ready(
            TokenUsageContract.TodayBilledTokens,
            FormatTokenCount(aggregate.TodayBilledTokens)) with
        {
            // This row's own share of the spend, not the day's total - the total is the
            // headline, and repeating it here would make the rows fail to add up.
            SecondaryText = FormatAmount(aggregate, aggregate.TodayBilledCostUsd),
        };
    }

    /// <summary>
    /// Today's cost at list price. Two decimal places up to four figures, then whole dollars -
    /// cents stop meaning anything once the total is in the hundreds, and the card has no room
    /// for digits that carry nothing.
    /// </summary>
    public static string FormatCost(TokenUsageAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return FormatAmount(aggregate, aggregate.TodayCostUsd);
    }

    /// <summary>
    /// One amount in the same shape as the headline, so a row's share and the day's total read
    /// as the same kind of number. The floor marker rides on every amount for the same reason
    /// it rides on the total: whatever could not be priced is missing from all of them.
    /// </summary>
    private static string FormatAmount(TokenUsageAggregate aggregate, decimal value)
    {
        if (value <= 0m)
        {
            return string.Empty;
        }

        decimal cost = value;
        string amount = cost >= 1000m
            ? cost.ToString("N0", CultureInfo.InvariantCulture)
            : cost.ToString("N2", CultureInfo.InvariantCulture);
        // A trailing plus where some of today's models have no verified price: the figure is
        // then a floor rather than a total, and saying so costs no height on a card that has
        // none to spare. Without it the number would quietly omit whatever could not be priced.
        string floorMarker = aggregate.UnpricedModelCount > 0 ? "+" : string.Empty;
        return "\u2248$" + amount + floorMarker;
    }

    private static TokenUsageMetricDto FormatOutputTokens(TokenUsageAggregate aggregate)
    {
        if (!HasToday(aggregate))
        {
            return Empty(TokenUsageContract.TodayOutputTokens);
        }

        // No ratio: the output count's share of billed tokens is a different kind of fact
        // than the hit rate's 0..100%, and two meters with two meanings on one card read as
        // one meaning. The hit rate keeps the card's only meter.
        return Ready(
            TokenUsageContract.TodayOutputTokens,
            FormatTokenCount(aggregate.TodayOutputTokens)) with
        {
            SecondaryText = FormatAmount(aggregate, aggregate.TodayOutputCostUsd),
        };
    }

    private static TokenUsageMetricDto FormatCacheReadTokens(TokenUsageAggregate aggregate)
    {
        if (!HasToday(aggregate))
        {
            return Empty(TokenUsageContract.TodayCacheReadTokens);
        }

        return Ready(
            TokenUsageContract.TodayCacheReadTokens,
            FormatTokenCount(aggregate.TodayCacheReadTokens)) with
        {
            // Usually the largest single line of the bill - about three quarters of it on the
            // reference machine - which is exactly why it carries its own amount.
            SecondaryText = FormatAmount(aggregate, aggregate.TodayCacheReadCostUsd),
        };
    }

    private static TokenUsageMetricDto FormatRequests(TokenUsageAggregate aggregate) =>
        HasToday(aggregate)
            ? Ready(
                TokenUsageContract.TodayRequests,
                aggregate.TodayRequests.ToString("N0", CultureInfo.InvariantCulture))
            : Empty(TokenUsageContract.TodayRequests);

    private static TokenUsageMetricDto FormatCacheHitRate(TokenUsageAggregate aggregate)
    {
        if (aggregate.CacheHitRate is not { } rate)
        {
            return Empty(TokenUsageContract.CacheHitRate);
        }

        return Ready(TokenUsageContract.CacheHitRate, FormatPercent(rate)) with
        {
            // The one metric here with a real ceiling, so the one that earns a meter.
            Ratio = Math.Clamp(rate, 0d, 1d),
        };
    }

    private static TokenUsageBreakdownDto[] FormatBreakdown(TokenUsageAggregate aggregate)
    {
        if (aggregate.Breakdown.Count == 0)
        {
            return Array.Empty<TokenUsageBreakdownDto>();
        }

        var rows = new TokenUsageBreakdownDto[aggregate.Breakdown.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            TokenUsageSlice slice = aggregate.Breakdown[i];
            double share = aggregate.TodayBilledTokens > 0
                ? Math.Clamp(slice.BilledTokens / (double)aggregate.TodayBilledTokens, 0d, 1d)
                : 0d;
            rows[i] = new TokenUsageBreakdownDto
            {
                Label = slice.Label,
                PrimaryText = FormatTokenCount(slice.BilledTokens),
                SecondaryText = FormatPercent(share),
                Ratio = share,
                CostText = FormatAmount(aggregate, slice.CostUsd),
            };
        }

        return rows;
    }

    private static bool HasToday(TokenUsageAggregate aggregate) =>
        aggregate.HasAnyRecord && aggregate.TodayRequests > 0;

    private static TokenUsageMetricDto Ready(string metricId, string primaryText) =>
        new()
        {
            MetricId = metricId,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = primaryText,
            SecondaryText = string.Empty,
            Ratio = null,
        };

    private static TokenUsageMetricDto Empty(string metricId) =>
        new()
        {
            MetricId = metricId,
            Status = TokenUsageMetricStatus.Empty,
            PrimaryText = Placeholder,
            SecondaryText = string.Empty,
            Ratio = null,
        };

    private static string Scale(long tokens, double divisor, string suffix)
    {
        double scaled = tokens / divisor;
        // Three significant digits: 9.87K, 98.7K, 987K. Keeps the column width stable while
        // the magnitude changes, which is what makes the card readable at a glance.
        int decimals = scaled < 10d ? 2 : scaled < 100d ? 1 : 0;
        return Round(scaled, decimals) + suffix;
    }

    private static string Round(double value, int decimals) =>
        Math.Round(value, decimals).ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
}
