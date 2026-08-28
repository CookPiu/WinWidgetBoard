using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Resource keys for the names the broker deliberately does not compose. It formats every
/// number but never a name: names are the part that has to be translated, and the panel owns
/// the resources.
/// </summary>
public static class TokenUsageResourceKeys
{
    public const string EmptyStatus = "TokenUsageStatus.Empty";
    public const string QuotaHeader = "TokenUsageQuota.Header";
    public const string QuotaCredits = "TokenUsageQuota.Credits";
    public const string QuotaResets = "TokenUsageQuota.Resets";
    public const string QuotaObserved = "TokenUsageQuota.Observed";
    public const string CostEstimate = "TokenUsageCost.Estimate";
    public const string CostUnpriced = "TokenUsageCost.Unpriced";

    public static string GetMetricNameKey(string metricId) => metricId switch
    {
        TokenUsageContract.TodayBilledTokens => "TokenUsageMetric.TodayBilled",
        TokenUsageContract.TodayOutputTokens => "TokenUsageMetric.TodayOutput",
        TokenUsageContract.TodayCacheReadTokens => "TokenUsageMetric.TodayCacheRead",
        TokenUsageContract.TodayRequests => "TokenUsageMetric.TodayRequests",
        TokenUsageContract.CacheHitRate => "TokenUsageMetric.CacheHitRate",
        TokenUsageContract.PeakRate => "TokenUsageMetric.PeakRate",
        _ => "TokenUsageMetric.Unknown",
    };

    /// <summary>
    /// The page tab's label. Vendor names are proper nouns and read the same in every language
    /// the panel ships, but they still come from resources - a name on screen is the panel's to
    /// own, and hard-coding two of them here is how the third one ends up untranslatable.
    /// </summary>
    public static string GetPageNameKey(string pageId) => pageId switch
    {
        TokenUsageContract.OverviewPageId => "TokenUsagePage.Overview",
        TokenUsageContract.ClaudeVendorId => "TokenUsagePage.Claude",
        TokenUsageContract.CodexVendorId => "TokenUsagePage.Codex",
        _ => "TokenUsagePage.Unknown",
    };
}

/// <summary>
/// One reading on a page. The visibility rules are resolved here rather than in XAML so they
/// stay testable without a WinUI host.
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

    /// <summary>
    /// Extra wording a screen reader needs that the visual row conveys by symbol - today's
    /// cost is shown with an approximation sign, and that is not enough on its own to say the
    /// figure is an estimate at list price rather than a bill.
    /// </summary>
    public string AutomationDetail { get; init; } = string.Empty;

    public string AutomationName
    {
        get
        {
            if (!HasReading)
            {
                return $"{Name} {MetricStatusText}";
            }

            string reading = IsSecondaryVisible
                ? $"{Name} {PrimaryText} {SecondaryText}"
                : $"{Name} {PrimaryText}";
            return AutomationDetail.Length > 0
                ? $"{reading} · {AutomationDetail}"
                : reading;
        }
    }
}

/// <summary>
/// One hour of the trend. <see cref="Fraction"/> arrives already scaled against the page's own
/// peak - the panel has no ceiling of its own to judge a token count against.
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

/// <summary>One slice of a page: a vendor on the overview, a model on a vendor's page.</summary>
public sealed record TokenUsageBreakdownRow
{
    public string Label { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public double Ratio { get; init; }

    public double MeterPercent => Math.Clamp(Ratio, 0d, 1d) * 100d;

    public string AutomationName => $"{Label} {PrimaryText} {SecondaryText}";
}

/// <summary>One quota window, as the vendor reported it.</summary>
public sealed record TokenUsageQuotaRow
{
    public string WindowId { get; init; } = string.Empty;

    public string WindowText { get; init; } = string.Empty;

    public string UsedText { get; init; } = string.Empty;

    public string ResetsAtText { get; init; } = string.Empty;

    /// <summary>Composed "resets at ..." line, or empty when no reset time was reported.</summary>
    public string ResetsLabel { get; init; } = string.Empty;

    public double UsedRatio { get; init; }

    public double MeterPercent => Math.Clamp(UsedRatio, 0d, 1d) * 100d;

    public bool IsResetVisible => ResetsLabel.Length > 0;

    public string AutomationName => IsResetVisible
        ? $"{WindowText} {UsedText} {ResetsLabel}"
        : $"{WindowText} {UsedText}";
}

/// <summary>
/// One view the card can page to: the overview, or a single vendor.
/// </summary>
public sealed record TokenUsagePage
{
    public string PageId { get; init; } = string.Empty;

    /// <summary>Localized tab label.</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<TokenUsageMetricRow> Metrics { get; init; } =
        Array.Empty<TokenUsageMetricRow>();

    public IReadOnlyList<TokenUsageTrendBar> Trend { get; init; } =
        Array.Empty<TokenUsageTrendBar>();

    public IReadOnlyList<TokenUsageBreakdownRow> Breakdown { get; init; } =
        Array.Empty<TokenUsageBreakdownRow>();

    public IReadOnlyList<TokenUsageQuotaRow> Quota { get; init; } =
        Array.Empty<TokenUsageQuotaRow>();

    public string QuotaCreditsText { get; init; } = string.Empty;

    public string QuotaObservedText { get; init; } = string.Empty;

    /// <summary>
    /// Today's usage at published list prices, already composed - "≈$12.34", or with a
    /// trailing "+" when some model could not be priced. Empty when nothing was priceable.
    /// </summary>
    public string CostText { get; init; } = string.Empty;

    /// <summary>Models in today's usage this build has no verified price for.</summary>
    public int UnpricedModelCount { get; init; }

    /// <summary>
    /// The wording that makes the figure honest: list price, not a bill. It rides on the
    /// reading's accessible name rather than taking a line of its own, because at L the card
    /// has no line to give.
    /// </summary>
    public string CostNoteText { get; init; } = string.Empty;

    public bool HasData { get; init; }

    /// <summary>
    /// The trend strip is hidden rather than shown flat when there is nothing in the window: a
    /// row of minimum-height bars looks like a reading of zero everywhere, which is not the
    /// same as having no history yet.
    /// </summary>
    public bool IsTrendVisible => Trend.Count > 0 && HasData;

    public bool IsBreakdownVisible => Breakdown.Count > 0;

    /// <summary>
    /// Only shown for a vendor that actually publishes quota, and only while the reading is
    /// still current. Absent is not "unknown quota" to be drawn as empty dials.
    /// </summary>
    public bool IsQuotaVisible => Quota.Count > 0 || QuotaCreditsText.Length > 0;

    public bool IsCreditsVisible => QuotaCreditsText.Length > 0;
}

public sealed record TokenUsageCardProjection
{
    private TokenUsageCardProjection(IReadOnlyList<TokenUsagePage> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<TokenUsagePage> Pages { get; }

    /// <summary>True when any page has something to report.</summary>
    public bool HasData => Pages.Any(page => page.HasData);

    /// <summary>
    /// The tab strip is pointless with a single page, which is what a board with one vendor
    /// switched on looks like: the overview would then just repeat that vendor's page.
    /// </summary>
    public bool IsPageSwitcherVisible => Pages.Count > 2;

    public static TokenUsageCardProjection Empty { get; } =
        new(Array.Empty<TokenUsagePage>());

    public static TokenUsageCardProjection FromSnapshot(
        CardRuntimeSnapshot snapshot,
        Func<string, string?> resourceResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resourceResolver);

        JsonElement payload = snapshot.Payload;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("pages", out JsonElement pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return Empty;
        }

        string emptyText = Resolve(resourceResolver, TokenUsageResourceKeys.EmptyStatus);
        var result = new List<TokenUsagePage>(pages.GetArrayLength());
        foreach (JsonElement page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? pageId = ReadString(page, "pageId");
            if (string.IsNullOrEmpty(pageId))
            {
                continue;
            }

            string costNote = ReadCostNote(page, resourceResolver);
            IReadOnlyList<TokenUsageMetricRow> metrics = ReadMetrics(
                page,
                resourceResolver,
                emptyText,
                costNote);
            result.Add(
                new TokenUsagePage
                {
                    PageId = pageId,
                    Name = Resolve(
                        resourceResolver,
                        TokenUsageResourceKeys.GetPageNameKey(pageId)),
                    Metrics = metrics,
                    Trend = ReadTrend(page),
                    Breakdown = ReadBreakdown(page),
                    Quota = ReadQuota(page, resourceResolver, out string credits, out string observed),
                    QuotaCreditsText = credits,
                    QuotaObservedText = observed,
                    CostText = ReadString(page, "costText") ?? string.Empty,
                    UnpricedModelCount = ReadInt(page, "unpricedModelCount"),
                    CostNoteText = costNote,
                    HasData = metrics.Any(metric => metric.HasReading),
                });
        }

        return result.Count == 0 ? Empty : new TokenUsageCardProjection(result);
    }

    private static TokenUsageMetricRow[] ReadMetrics(
        JsonElement page,
        Func<string, string?> resourceResolver,
        string emptyText,
        string costNote)
    {
        if (!page.TryGetProperty("metrics", out JsonElement metrics) ||
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
                        TokenUsageResourceKeys.GetMetricNameKey(metricId)),
                    Status = ReadString(metric, "status") ?? TokenUsageMetricStatus.Empty,
                    PrimaryText = ReadString(metric, "primaryText") ?? "—",
                    SecondaryText = ReadString(metric, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(metric, "ratio"),
                    MetricStatusText = emptyText,
                    AutomationDetail = string.Equals(
                        metricId,
                        TokenUsageContract.TodayBilledTokens,
                        StringComparison.Ordinal)
                        ? costNote
                        : string.Empty,
                });
        }

        return rows.ToArray();
    }

    private static TokenUsageTrendBar[] ReadTrend(JsonElement page)
    {
        if (!page.TryGetProperty("trend", out JsonElement trend) ||
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

    private static TokenUsageBreakdownRow[] ReadBreakdown(JsonElement page)
    {
        if (!page.TryGetProperty("breakdown", out JsonElement breakdown) ||
            breakdown.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageBreakdownRow>();
        }

        var rows = new List<TokenUsageBreakdownRow>(breakdown.GetArrayLength());
        foreach (JsonElement slice in breakdown.EnumerateArray())
        {
            if (slice.ValueKind != JsonValueKind.Object ||
                rows.Count >= TokenUsageContract.MaxBreakdownRows)
            {
                continue;
            }

            string? label = ReadString(slice, "label");
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            rows.Add(
                new TokenUsageBreakdownRow
                {
                    Label = label,
                    PrimaryText = ReadString(slice, "primaryText") ?? "—",
                    SecondaryText = ReadString(slice, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(slice, "ratio") ?? 0d,
                });
        }

        return rows.ToArray();
    }

    private static TokenUsageQuotaRow[] ReadQuota(
        JsonElement page,
        Func<string, string?> resourceResolver,
        out string creditsText,
        out string observedText)
    {
        creditsText = string.Empty;
        observedText = string.Empty;
        if (!page.TryGetProperty("quota", out JsonElement quota) ||
            quota.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<TokenUsageQuotaRow>();
        }

        string credits = ReadString(quota, "creditsText") ?? string.Empty;
        // Labelled, because an unlabelled number sitting in a row of percentages reads as
        // another percentage.
        creditsText = credits.Length > 0
            ? Format(resourceResolver, TokenUsageResourceKeys.QuotaCredits, credits)
            : string.Empty;
        string observed = ReadString(quota, "observedAtText") ?? string.Empty;
        if (observed.Length > 0)
        {
            observedText = Format(
                resourceResolver,
                TokenUsageResourceKeys.QuotaObserved,
                observed);
        }

        if (!quota.TryGetProperty("windows", out JsonElement windows) ||
            windows.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageQuotaRow>();
        }

        var rows = new List<TokenUsageQuotaRow>(windows.GetArrayLength());
        foreach (JsonElement window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string resets = ReadString(window, "resetsAtText") ?? string.Empty;
            rows.Add(
                new TokenUsageQuotaRow
                {
                    WindowId = ReadString(window, "windowId") ?? string.Empty,
                    WindowText = ReadString(window, "windowText") ?? string.Empty,
                    UsedText = ReadString(window, "usedText") ?? "—",
                    ResetsAtText = resets,
                    ResetsLabel = resets.Length > 0
                        ? Format(resourceResolver, TokenUsageResourceKeys.QuotaResets, resets)
                        : string.Empty,
                    UsedRatio = ReadRatio(window, "usedRatio") ?? 0d,
                });
        }

        return rows.ToArray();
    }

    /// <summary>
    /// The estimate wording for this page: always that the figure is list price rather than a
    /// bill, and additionally how many models had no price when any did not.
    /// </summary>
    private static string ReadCostNote(
        JsonElement page,
        Func<string, string?> resourceResolver)
    {
        string cost = ReadString(page, "costText") ?? string.Empty;
        int unpriced = ReadInt(page, "unpricedModelCount");
        if (cost.Length == 0 && unpriced == 0)
        {
            return string.Empty;
        }

        string note = Resolve(resourceResolver, TokenUsageResourceKeys.CostEstimate);
        return unpriced > 0
            ? note + " " + Format(
                resourceResolver,
                TokenUsageResourceKeys.CostUnpriced,
                unpriced.ToString(System.Globalization.CultureInfo.CurrentCulture))
            : note;
    }

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsed) &&
            parsed >= 0
            ? parsed
            : 0;

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

    /// <summary>
    /// Fills one placeholder in a localized template. The broker composes the value and the
    /// panel composes the sentence around it, which is what keeps word order translatable.
    /// </summary>
    private static string Format(Func<string, string?> resolver, string key, string value)
    {
        string template = Resolve(resolver, key);
        return template.Contains("{0}", StringComparison.Ordinal)
            ? template.Replace("{0}", value, StringComparison.Ordinal)
            : template + " " + value;
    }
}
