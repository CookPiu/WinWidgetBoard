using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Resource keys for the metric names and the one non-ready state. The broker formats every
/// number but never a name: names are the part that has to be translated, and the panel owns
/// the resources.
/// </summary>
public static class TokenUsageResourceKeys
{
    public const string EmptyStatus = "TokenUsageStatus.Empty";

    public static string GetNameKey(string metricId) => metricId switch
    {
        TokenUsageContract.TodayBilledTokens => "TokenUsageMetric.TodayBilled",
        TokenUsageContract.TodayOutputTokens => "TokenUsageMetric.TodayOutput",
        TokenUsageContract.TodayCacheReadTokens => "TokenUsageMetric.TodayCacheRead",
        TokenUsageContract.TodayRequests => "TokenUsageMetric.TodayRequests",
        TokenUsageContract.CacheHitRate => "TokenUsageMetric.CacheHitRate",
        TokenUsageContract.CurrentRate => "TokenUsageMetric.CurrentRate",
        TokenUsageContract.PeakRate => "TokenUsageMetric.PeakRate",
        _ => "TokenUsageMetric.Unknown",
    };
}

/// <summary>
/// One reading of the token-usage card. The visibility rules are resolved here rather than in
/// XAML so they stay testable without a WinUI host.
/// </summary>
public sealed record TokenUsageMetricRow
{
    public string MetricId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = TokenUsageMetricStatus.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public double? Ratio { get; init; }

    /// <summary>Localized wording for a reading there is currently nothing to report for.</summary>
    public string MetricStatusText { get; init; } = string.Empty;

    public bool HasReading => string.Equals(
        Status,
        TokenUsageMetricStatus.Ready,
        StringComparison.Ordinal);

    /// <summary>
    /// Only a reading with a real ceiling earns a meter. A token count and a rate have none,
    /// and a bar drawn against an invented maximum would assert something the data does not.
    /// </summary>
    public bool IsMeterVisible => HasReading && Ratio.HasValue;

    public bool IsSecondaryVisible => HasReading && SecondaryText.Length > 0;

    public bool IsMetricStatusTextVisible => !HasReading;

    public double MeterFraction => Ratio ?? 0d;

    public string AutomationName => HasReading
        ? IsSecondaryVisible
            ? $"{Name} {PrimaryText} {SecondaryText}"
            : $"{Name} {PrimaryText}"
        : $"{Name} {MetricStatusText}";
}

/// <summary>
/// One hour of the trend. <see cref="Fraction"/> arrives already scaled against the window's
/// own peak - the panel has no ceiling of its own to judge a token count against.
/// </summary>
public sealed record TokenUsageTrendBar
{
    /// <summary>
    /// The bar strip's height in DIPs. It lives here rather than in XAML because the bar
    /// heights are computed, and one number cannot be the source of truth in two places.
    /// </summary>
    public const double TrackHeight = 32d;

    /// <summary>
    /// A bar is never fully invisible: an hour with a little usage and an hour with none read
    /// the same at zero height, and the difference is the interesting part.
    /// </summary>
    public const double MinimumBarHeight = 1d;

    public int Index { get; init; }

    public double Fraction { get; init; }

    public double BarHeight => Math.Max(
        MinimumBarHeight,
        Math.Clamp(Fraction, 0d, 1d) * TrackHeight);
}

public sealed record TokenUsageModelRow
{
    public string Model { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public double Ratio { get; init; }

    public double MeterPercent => Math.Clamp(Ratio, 0d, 1d) * 100d;

    public string AutomationName => $"{Model} {PrimaryText} {SecondaryText}";
}

public sealed record TokenUsageCardProjection
{
    private TokenUsageCardProjection(
        IReadOnlyList<TokenUsageMetricRow> metrics,
        IReadOnlyList<TokenUsageTrendBar> trend,
        IReadOnlyList<TokenUsageModelRow> models,
        bool hasData)
    {
        Metrics = metrics;
        Trend = trend;
        Models = models;
        HasData = hasData;
    }

    public IReadOnlyList<TokenUsageMetricRow> Metrics { get; }

    public IReadOnlyList<TokenUsageTrendBar> Trend { get; }

    public IReadOnlyList<TokenUsageModelRow> Models { get; }

    public bool HasData { get; }

    /// <summary>
    /// The trend strip is hidden rather than shown flat when there is nothing in the window:
    /// a row of minimum-height bars looks like a reading of zero everywhere, which is not the
    /// same as having no history yet.
    /// </summary>
    public bool IsTrendVisible => Trend.Count > 0 && HasData;

    public bool AreModelsVisible => Models.Count > 0;

    public static TokenUsageCardProjection Empty { get; } =
        new(
            Array.Empty<TokenUsageMetricRow>(),
            Array.Empty<TokenUsageTrendBar>(),
            Array.Empty<TokenUsageModelRow>(),
            hasData: false);

    public static TokenUsageCardProjection FromSnapshot(
        CardRuntimeSnapshot snapshot,
        Func<string, string?> resourceResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resourceResolver);

        JsonElement payload = snapshot.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        string emptyText = Resolve(resourceResolver, TokenUsageResourceKeys.EmptyStatus);
        IReadOnlyList<TokenUsageMetricRow> metrics = ReadMetrics(
            payload,
            resourceResolver,
            emptyText);
        bool hasData = metrics.Any(metric => metric.HasReading);

        return new TokenUsageCardProjection(
            metrics,
            ReadTrend(payload),
            ReadModels(payload),
            hasData);
    }

    private static TokenUsageMetricRow[] ReadMetrics(
        JsonElement payload,
        Func<string, string?> resourceResolver,
        string emptyText)
    {
        if (!payload.TryGetProperty("metrics", out JsonElement metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageMetricRow>();
        }

        var rows = new List<TokenUsageMetricRow>(metrics.GetArrayLength());
        foreach (JsonElement metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? metricId = ReadString(metric, "metricId");
            if (metricId is null || !TokenUsageContract.IsKnownMetricId(metricId))
            {
                // A newer broker can report a reading this build has no name for. Dropping it
                // beats showing a number nobody can label.
                continue;
            }

            rows.Add(
                new TokenUsageMetricRow
                {
                    MetricId = metricId,
                    Name = Resolve(
                        resourceResolver,
                        TokenUsageResourceKeys.GetNameKey(metricId)),
                    Status = ReadString(metric, "status") ?? TokenUsageMetricStatus.Empty,
                    PrimaryText = ReadString(metric, "primaryText") ?? "—",
                    SecondaryText = ReadString(metric, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(metric, "ratio"),
                    MetricStatusText = emptyText,
                });
        }

        return rows.ToArray();
    }

    private static TokenUsageTrendBar[] ReadTrend(JsonElement payload)
    {
        if (!payload.TryGetProperty("trend", out JsonElement trend) ||
            trend.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageTrendBar>();
        }

        var bars = new List<TokenUsageTrendBar>(trend.GetArrayLength());
        int index = 0;
        foreach (JsonElement value in trend.EnumerateArray())
        {
            if (index >= TokenUsageContract.TrendHours)
            {
                // A newer broker could send a longer window than this build lays out.
                break;
            }

            double fraction = value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double parsed) &&
                double.IsFinite(parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : 0d;
            bars.Add(new TokenUsageTrendBar { Index = index, Fraction = fraction });
            index++;
        }

        return bars.ToArray();
    }

    private static TokenUsageModelRow[] ReadModels(JsonElement payload)
    {
        if (!payload.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageModelRow>();
        }

        var rows = new List<TokenUsageModelRow>(models.GetArrayLength());
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                rows.Count >= TokenUsageContract.MaxModelRows)
            {
                continue;
            }

            string? name = ReadString(model, "model");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            rows.Add(
                new TokenUsageModelRow
                {
                    Model = name,
                    PrimaryText = ReadString(model, "primaryText") ?? "—",
                    SecondaryText = ReadString(model, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(model, "ratio") ?? 0d,
                });
        }

        return rows.ToArray();
    }

    private static double? ReadRatio(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !value.TryGetDouble(out double ratio) ||
            !double.IsFinite(ratio))
        {
            return null;
        }

        return Math.Clamp(ratio, 0d, 1d);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Resolve(Func<string, string?> resolver, string key)
    {
        string? value = resolver(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }
}
