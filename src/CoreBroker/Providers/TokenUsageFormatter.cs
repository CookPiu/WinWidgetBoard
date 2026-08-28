using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Turns a report into the exact strings the card displays, and normalises each page's trend
/// into the 0..1 it can draw. The broker owns units and rounding here for the same reason
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
                quota: null,
                zone,
                sampledAtUtc),
        };

        foreach (TokenUsageVendorReport vendor in report.Vendors)
        {
            pages.Add(
                CreatePage(
                    vendor.VendorId,
                    vendor.Aggregate,
                    vendor.Quota,
                    zone,
                    sampledAtUtc));
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
        VendorQuotaSnapshot? quota,
        TimeZoneInfo? timeZone = null,
        DateTimeOffset nowUtc = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        return new TokenUsagePageDto
        {
            PageId = pageId,
            // Priority order, matching TokenUsageContract.MetricIds: a card too short for all
            // of them shows a prefix, so the most useful readings have to come first.
            Metrics =
            [
                FormatBilledTokens(aggregate),
                FormatCurrentRate(aggregate),
                FormatCacheHitRate(aggregate),
                FormatRequests(aggregate),
                FormatOutputTokens(aggregate),
                FormatCacheReadTokens(aggregate),
                FormatPeakRate(aggregate),
            ],
            Trend = NormalizeTrend(aggregate.Trend),
            Breakdown = FormatBreakdown(aggregate),
            Quota = FormatQuota(quota, timeZone ?? TimeZoneInfo.Local, nowUtc),
            CostText = FormatCost(aggregate),
            UnpricedModelCount = aggregate.UnpricedModelCount,
        };
    }

    /// <summary>
    /// Scales a page's hourly totals against that page's own peak. There is no absolute ceiling
    /// a token count could be drawn against, so the alternative would be inventing one; a bar
    /// chart of the last day against its own maximum is the honest shape.
    /// </summary>
    public static double[] NormalizeTrend(IReadOnlyList<TokenUsageHourBucket> trend)
    {
        ArgumentNullException.ThrowIfNull(trend);
        if (trend.Count == 0)
        {
            return Array.Empty<double>();
        }

        long peak = 0;
        foreach (TokenUsageHourBucket bucket in trend)
        {
            if (bucket.BilledTokens > peak)
            {
                peak = bucket.BilledTokens;
            }
        }

        var normalized = new double[trend.Count];
        if (peak <= 0)
        {
            // A flat day of zeros, not a missing reading. The card draws an empty axis.
            return normalized;
        }

        for (int i = 0; i < trend.Count; i++)
        {
            normalized[i] = Math.Clamp(trend[i].BilledTokens / (double)peak, 0d, 1d);
        }

        return normalized;
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

    /// <summary>
    /// A quota window's length, language-neutral: "5h", "7d", "45m". The vendor reports it in
    /// minutes and the card has no room for a sentence.
    /// </summary>
    public static string FormatWindowLength(int minutes)
    {
        if (minutes <= 0)
        {
            return string.Empty;
        }

        if (minutes % 1440 == 0)
        {
            return (minutes / 1440).ToString(CultureInfo.InvariantCulture) + "d";
        }

        if (minutes % 60 == 0)
        {
            return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h";
        }

        return minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    private static TokenUsageQuotaDto? FormatQuota(
        VendorQuotaSnapshot? quota,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc)
    {
        if (quota is null || quota.Windows.Count == 0)
        {
            // Absent means the vendor never told us. Rendering an empty dial instead would
            // imply a limit the card cannot actually see.
            return null;
        }

        var windows = new List<TokenUsageQuotaWindowDto>(quota.Windows.Count);
        foreach (VendorQuotaWindow window in quota.Windows)
        {
            // Quota is read from session records rather than queried, so a window whose reset
            // instant has already passed says nothing about the present: it certainly reset,
            // and by how much it has since refilled is not something this card can know. It is
            // dropped rather than shown, on the same principle as a vendor that reports no
            // quota at all - a stale "100% used" is worse than saying nothing.
            if (window.ResetsAtUtc is { } resetsAt && resetsAt <= nowUtc)
            {
                continue;
            }

            double ratio = Math.Clamp(window.UsedPercent / 100d, 0d, 1d);
            windows.Add(
                new TokenUsageQuotaWindowDto
                {
                    WindowId = window.WindowId,
                    WindowText = FormatWindowLength(window.WindowMinutes),
                    UsedText = FormatPercent(ratio),
                    UsedRatio = ratio,
                    ResetsAtText = FormatResetInstant(window.ResetsAtUtc, timeZone, nowUtc),
                });
        }

        // Credits do not expire on a window, so they survive every window going stale; with
        // neither left there is nothing to show.
        string credits = quota.CreditsBalance ?? string.Empty;
        if (windows.Count == 0 && credits.Length == 0)
        {
            return null;
        }

        return new TokenUsageQuotaDto
        {
            Windows = windows,
            CreditsText = credits,
            ObservedAtText = FormatObservedInstant(quota.ObservedAtUtc, timeZone),
        };
    }

    /// <summary>
    /// A reset instant as a wall-clock time rather than a countdown: the payload is refreshed
    /// on a cadence, and a countdown baked into a string is wrong the moment it is rendered.
    /// The date is added only when the reset is not today, which is what tells a five-hour
    /// window apart from a weekly one at a glance.
    /// </summary>
    private static string FormatResetInstant(
        DateTimeOffset? instantUtc,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc)
    {
        if (instantUtc is not { } instant)
        {
            return string.Empty;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(instant, timeZone);
        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        return local.Date == nowLocal.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatObservedInstant(
        DateTimeOffset instantUtc,
        TimeZoneInfo timeZone) =>
        instantUtc == default
            ? string.Empty
            : TimeZoneInfo.ConvertTime(instantUtc, timeZone)
                .ToString("HH:mm", CultureInfo.InvariantCulture);

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
            // The approximation sign is doing real work: this is list price, not a bill.
            SecondaryText = FormatCost(aggregate),
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
        if (aggregate.TodayCostUsd <= 0m)
        {
            return string.Empty;
        }

        decimal cost = aggregate.TodayCostUsd;
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

        return Ready(
            TokenUsageContract.TodayOutputTokens,
            FormatTokenCount(aggregate.TodayOutputTokens)) with
        {
            Ratio = aggregate.TodayBilledTokens > 0
                ? Math.Clamp(
                    aggregate.TodayOutputTokens / (double)aggregate.TodayBilledTokens,
                    0d,
                    1d)
                : null,
        };
    }

    private static TokenUsageMetricDto FormatCacheReadTokens(TokenUsageAggregate aggregate) =>
        HasToday(aggregate)
            ? Ready(
                TokenUsageContract.TodayCacheReadTokens,
                FormatTokenCount(aggregate.TodayCacheReadTokens))
            : Empty(TokenUsageContract.TodayCacheReadTokens);

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

    private static TokenUsageMetricDto FormatCurrentRate(TokenUsageAggregate aggregate) =>
        aggregate.HasAnyRecord
            // An idle window reports zero rather than a placeholder: "nothing has run for the
            // last quarter of an hour" is a reading, and one the user asked to see.
            ? Ready(
                TokenUsageContract.CurrentRate,
                FormatRate(aggregate.CurrentRatePerMinute))
            : Empty(TokenUsageContract.CurrentRate);

    private static TokenUsageMetricDto FormatPeakRate(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord ||
            aggregate.PeakWindowStartLocal is not { } peakWindow ||
            aggregate.PeakRatePerMinute <= 0)
        {
            return Empty(TokenUsageContract.PeakRate);
        }

        return Ready(
            TokenUsageContract.PeakRate,
            FormatRate(aggregate.PeakRatePerMinute)) with
        {
            SecondaryText = peakWindow.ToString("HH:mm", CultureInfo.InvariantCulture),
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
