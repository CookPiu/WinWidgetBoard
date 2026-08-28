using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Turns an aggregate into the exact strings the card displays, and normalises the trend into
/// the 0..1 the card can draw. The broker owns units and rounding here for the same reason
/// <see cref="SystemMonitorFormatter"/> does: the card should bind text rather than re-derive
/// numbers, and there is then one place where a unit is decided.
///
/// Every unit produced here is language-neutral - K, M, B, %, /min and a 24-hour clock - so
/// nothing in this file needs translating. The metric names are the part that does, and those
/// stay in the panel's own resources.
///
/// Pure: no clock, no I/O, no state. The whole formatting surface is unit-testable.
/// </summary>
public static class TokenUsageFormatter
{
    /// <summary>Shown wherever a reading exists in principle but there is nothing to report.</summary>
    public const string Placeholder = "—";

    public static TokenUsageCardPayloadDto CreatePayload(
        TokenUsageAggregate aggregate,
        DateTimeOffset sampledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        return new TokenUsageCardPayloadDto
        {
            Metrics =
            [
                FormatBilledTokens(aggregate),
                FormatOutputTokens(aggregate),
                FormatCacheReadTokens(aggregate),
                FormatRequests(aggregate),
                FormatCacheHitRate(aggregate),
                FormatCurrentRate(aggregate),
                FormatPeakRate(aggregate),
            ],
            Trend = NormalizeTrend(aggregate.Trend),
            Models = FormatModels(aggregate),
            SampledAtUtc = sampledAtUtc.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Scales the hourly totals against the window's own peak. There is no absolute ceiling a
    /// token count could be drawn against, so the alternative would be inventing one; a bar
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
    public static string FormatRate(double tokensPerMinute)
    {
        if (!double.IsFinite(tokensPerMinute) || tokensPerMinute < 0)
        {
            return Placeholder;
        }

        return FormatTokenCount((long)Math.Round(tokensPerMinute)) + "/min";
    }

    public static string FormatPercent(double ratio) =>
        double.IsFinite(ratio)
            ? Round(Math.Clamp(ratio, 0d, 1d) * 100d, 1) + "%"
            : Placeholder;

    private static TokenUsageMetricDto FormatBilledTokens(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord || aggregate.TodayRequests == 0)
        {
            return Empty(TokenUsageContract.TodayBilledTokens);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.TodayBilledTokens,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatTokenCount(aggregate.TodayBilledTokens),
            SecondaryText = string.Empty,
            Ratio = null,
        };
    }

    private static TokenUsageMetricDto FormatOutputTokens(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord || aggregate.TodayRequests == 0)
        {
            return Empty(TokenUsageContract.TodayOutputTokens);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.TodayOutputTokens,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatTokenCount(aggregate.TodayOutputTokens),
            SecondaryText = string.Empty,
            Ratio = aggregate.TodayBilledTokens > 0
                ? Math.Clamp(
                    aggregate.TodayOutputTokens / (double)aggregate.TodayBilledTokens,
                    0d,
                    1d)
                : null,
        };
    }

    private static TokenUsageMetricDto FormatCacheReadTokens(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord || aggregate.TodayRequests == 0)
        {
            return Empty(TokenUsageContract.TodayCacheReadTokens);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.TodayCacheReadTokens,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatTokenCount(aggregate.TodayCacheReadTokens),
            SecondaryText = string.Empty,
            Ratio = null,
        };
    }

    private static TokenUsageMetricDto FormatRequests(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord || aggregate.TodayRequests == 0)
        {
            return Empty(TokenUsageContract.TodayRequests);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.TodayRequests,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = aggregate.TodayRequests.ToString(
                "N0",
                CultureInfo.InvariantCulture),
            SecondaryText = string.Empty,
            Ratio = null,
        };
    }

    private static TokenUsageMetricDto FormatCacheHitRate(TokenUsageAggregate aggregate)
    {
        if (aggregate.CacheHitRate is not { } rate)
        {
            return Empty(TokenUsageContract.CacheHitRate);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.CacheHitRate,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatPercent(rate),
            SecondaryText = string.Empty,
            // The one metric here with a real ceiling, so the one that earns a meter.
            Ratio = Math.Clamp(rate, 0d, 1d),
        };
    }

    private static TokenUsageMetricDto FormatCurrentRate(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord)
        {
            return Empty(TokenUsageContract.CurrentRate);
        }

        // An idle window reports zero rather than a placeholder: "nothing has run for the last
        // quarter of an hour" is a reading, and one the user asked to see.
        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.CurrentRate,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatRate(aggregate.CurrentRatePerMinute),
            SecondaryText = string.Empty,
            Ratio = null,
        };
    }

    private static TokenUsageMetricDto FormatPeakRate(TokenUsageAggregate aggregate)
    {
        if (!aggregate.HasAnyRecord ||
            aggregate.PeakWindowStartLocal is not { } peakWindow ||
            aggregate.PeakRatePerMinute <= 0)
        {
            return Empty(TokenUsageContract.PeakRate);
        }

        return new TokenUsageMetricDto
        {
            MetricId = TokenUsageContract.PeakRate,
            Status = TokenUsageMetricStatus.Ready,
            PrimaryText = FormatRate(aggregate.PeakRatePerMinute),
            SecondaryText = peakWindow.ToString("HH:mm", CultureInfo.InvariantCulture),
            Ratio = null,
        };
    }

    private static TokenUsageModelDto[] FormatModels(TokenUsageAggregate aggregate)
    {
        if (aggregate.Models.Count == 0)
        {
            return Array.Empty<TokenUsageModelDto>();
        }

        var rows = new TokenUsageModelDto[aggregate.Models.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            TokenUsageModelTotal model = aggregate.Models[i];
            double share = aggregate.TodayBilledTokens > 0
                ? Math.Clamp(model.BilledTokens / (double)aggregate.TodayBilledTokens, 0d, 1d)
                : 0d;
            rows[i] = new TokenUsageModelDto
            {
                Model = model.Model,
                PrimaryText = FormatTokenCount(model.BilledTokens),
                SecondaryText = FormatPercent(share),
                Ratio = share,
            };
        }

        return rows;
    }

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
