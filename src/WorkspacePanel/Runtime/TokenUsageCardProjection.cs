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
    public const string CostEstimate = "TokenUsageCost.Estimate";
    public const string CostUnpriced = "TokenUsageCost.Unpriced";
    public const string CacheReadNote = "TokenUsageKpi.CacheReadNote";
    public const string AveragePerRequest = "TokenUsageKpi.AveragePerRequest";
    public const string CurveNow = "TokenUsageCurve.Now";
    public const string CurveHourTokens = "TokenUsageCurve.HourTokens";

    public static string GetMetricNameKey(string metricId) => metricId switch
    {
        TokenUsageContract.TodayBilledTokens => "TokenUsageMetric.TodayBilled",
        TokenUsageContract.TodayOutputTokens => "TokenUsageMetric.TodayOutput",
        TokenUsageContract.TodayCacheReadTokens => "TokenUsageMetric.TodayCacheRead",
        TokenUsageContract.TodayRequests => "TokenUsageMetric.TodayRequests",
        TokenUsageContract.CacheHitRate => "TokenUsageMetric.CacheHitRate",
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
/// One point of the day's spend curve, already placed on 0..1 axes by the broker. The tip is
/// composed here because it holds the one word that has to be translated - "now" - and the
/// hour-tokens phrase around a number the broker formatted.
/// </summary>
public sealed record TokenUsageSpendPoint
{
    public int Hour { get; init; }

    /// <summary>Horizontal position, 0..1; the last point is always at 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Vertical position, 0..1; the last point is always at 1.</summary>
    public double Level { get; init; }

    public bool IsCurrent { get; init; }

    /// <summary>What the crosshair says at this point: the hour, the spend through it, the
    /// tokens within it.</summary>
    public string TipText { get; init; } = string.Empty;
}

/// <summary>One slice of a page: a vendor on the overview, a model on a vendor's page.</summary>
public sealed record TokenUsageBreakdownRow
{
    /// <summary>
    /// The vendor's display name on the overview, the raw model id on a vendor page. A vendor
    /// id is one of two the panel knows and has a resource for; a model id is whatever the
    /// session recorded and is shown as such.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public double Ratio { get; init; }

    /// <summary>This slice's spend, formatted like the headline; empty when unpriced.</summary>
    public string CostText { get; init; } = string.Empty;

    public double MeterPercent => Math.Clamp(Ratio, 0d, 1d) * 100d;

    /// <summary>
    /// The label the split meter carries: the slice's spend where it has one, its token count
    /// where it does not, so the meter's two ends read in the same currency as the headline
    /// whenever that is possible.
    /// </summary>
    public string SplitLabel => CostText.Length > 0
        ? $"{Label} {CostText}"
        : $"{Label} {PrimaryText}";

    public string AutomationName => $"{Label} {PrimaryText} {SecondaryText}";
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

    public IReadOnlyList<TokenUsageSpendPoint> SpendCurve { get; init; } =
        Array.Empty<TokenUsageSpendPoint>();

    public IReadOnlyList<TokenUsageBreakdownRow> Breakdown { get; init; } =
        Array.Empty<TokenUsageBreakdownRow>();

    /// <summary>Every token today, billed and cache reads together. Empty without data.</summary>
    public string TotalTokensText { get; init; } = string.Empty;

    /// <summary>The line under the total: how much of it was cache reads.</summary>
    public string TotalTokensNoteText { get; init; } = string.Empty;

    /// <summary>Responses today, lifted out of the rows on a card tall enough for tiles.</summary>
    public string RequestsText { get; init; } = string.Empty;

    /// <summary>The line under the responses: billed tokens per response.</summary>
    public string RequestsNoteText { get; init; } = string.Empty;

    /// <summary>
    /// Whether the two tiles have anything to say. They are a pair: one with a figure and
    /// one without would leave a hole where the other belongs.
    /// </summary>
    public bool HasKpi => TotalTokensText.Length > 0 && RequestsText.Length > 0;

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
    /// The curve is hidden rather than drawn flat when there is nothing today: a line along
    /// the floor looks like a reading of zero everywhere, which is not the same as having no
    /// history yet.
    /// </summary>
    public bool IsSpendCurveVisible => SpendCurve.Count > 0 && HasData;

    public bool IsBreakdownVisible => Breakdown.Count > 0;
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
            string totalTokens = ReadString(page, "totalTokensText") ?? string.Empty;
            string averageBilled =
                ReadString(page, "averageBilledPerRequestText") ?? string.Empty;
            TokenUsageMetricRow? requests = metrics.FirstOrDefault(metric =>
                string.Equals(
                    metric.MetricId,
                    TokenUsageContract.TodayRequests,
                    StringComparison.Ordinal));
            TokenUsageMetricRow? cacheRead = metrics.FirstOrDefault(metric =>
                string.Equals(
                    metric.MetricId,
                    TokenUsageContract.TodayCacheReadTokens,
                    StringComparison.Ordinal));
            result.Add(
                new TokenUsagePage
                {
                    PageId = pageId,
                    Name = Resolve(
                        resourceResolver,
                        TokenUsageResourceKeys.GetPageNameKey(pageId)),
                    Metrics = metrics,
                    SpendCurve = ReadSpendCurve(page, resourceResolver),
                    Breakdown = ReadBreakdown(page, resourceResolver),
                    TotalTokensText = totalTokens,
                    TotalTokensNoteText = totalTokens.Length > 0 && cacheRead?.HasReading == true
                        ? Format(
                            resourceResolver,
                            TokenUsageResourceKeys.CacheReadNote,
                            cacheRead.PrimaryText)
                        : string.Empty,
                    RequestsText = requests?.HasReading == true
                        ? requests.PrimaryText
                        : string.Empty,
                    RequestsNoteText = averageBilled.Length > 0
                        ? Format(
                            resourceResolver,
                            TokenUsageResourceKeys.AveragePerRequest,
                            averageBilled)
                        : string.Empty,
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

    /// <summary>
    /// The curve's points, in the order the broker placed them. The panel trusts the placement
    /// and only composes the words: a point whose position is not a number is dropped rather
    /// than drawn at the origin, because a point at the origin is a claim about midnight.
    /// </summary>
    private static TokenUsageSpendPoint[] ReadSpendCurve(
        JsonElement page,
        Func<string, string?> resourceResolver)
    {
        if (!page.TryGetProperty("spendCurve", out JsonElement curve) ||
            curve.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TokenUsageSpendPoint>();
        }

        var points = new List<TokenUsageSpendPoint>(curve.GetArrayLength());
        foreach (JsonElement element in curve.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                points.Count >= TokenUsageContract.TrendHours ||
                ReadRatio(element, "fraction") is not { } fraction ||
                ReadRatio(element, "level") is not { } level)
            {
                continue;
            }

            int hour = Math.Clamp(ReadInt(element, "hour"), 0, 23);
            bool isCurrent = element.TryGetProperty("isCurrent", out JsonElement current) &&
                current.ValueKind == JsonValueKind.True;
            string cost = ReadString(element, "cumulativeCostText") ?? string.Empty;
            string billed = ReadString(element, "billedText") ?? string.Empty;
            string label = isCurrent
                ? Resolve(resourceResolver, TokenUsageResourceKeys.CurveNow)
                : hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":00";
            string tokens = billed.Length > 0
                ? Format(resourceResolver, TokenUsageResourceKeys.CurveHourTokens, billed)
                : string.Empty;
            points.Add(
                new TokenUsageSpendPoint
                {
                    Hour = hour,
                    Fraction = fraction,
                    Level = level,
                    IsCurrent = isCurrent,
                    TipText = string.Join(
                        " · ",
                        new[] { cost.Length > 0 ? label + " " + cost : label, tokens }
                            .Where(part => part.Length > 0)),
                });
        }

        return points.ToArray();
    }

    private static TokenUsageBreakdownRow[] ReadBreakdown(
        JsonElement page,
        Func<string, string?> resourceResolver)
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
                    Label = TokenUsageContract.IsKnownVendorId(label)
                        ? Resolve(
                            resourceResolver,
                            TokenUsageResourceKeys.GetPageNameKey(label))
                        : label,
                    PrimaryText = ReadString(slice, "primaryText") ?? "—",
                    SecondaryText = ReadString(slice, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(slice, "ratio") ?? 0d,
                    CostText = ReadString(slice, "costText") ?? string.Empty,
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
