namespace WinWidgetBoard.Contracts.Protocol;

/// <summary>
/// Local token-usage accounting, read from the agent transcripts this machine already writes.
/// Metric identifiers are the table shared by the broker and the panel card, for the same
/// reason <see cref="SystemMonitorContract"/> shares its own: a raw column name on the wire
/// would give each side its own copy to drift.
///
/// Nothing here reaches a network and nothing is persisted. The transcripts hold entire
/// conversations - source code, paths, possibly secrets - so the scanner lifts four counters,
/// a model name and a timestamp per response and never materialises message content.
/// </summary>
public static class TokenUsageContract
{
    public const string SettingsGetMethod = "tokenusage.settings.get";
    public const string SettingsSaveMethod = "tokenusage.settings.save";

    public const int MaxInstanceIdLength = CardsContract.MaxInstanceIdLength;
    public const string DefaultInstanceId = "demo.tokenusage";

    // --- vendors ---------------------------------------------------------------------------

    /// <summary>
    /// The page that sums every enabled vendor. It is a page id, not a vendor id: nothing is
    /// ever scanned or configured under this name.
    /// </summary>
    public const string OverviewPageId = "overview";

    public const string ClaudeVendorId = "claude";
    public const string CodexVendorId = "codex";

    public const int MaxVendorIdLength = 32;

    /// <summary>
    /// Every vendor this build can read, in the order the card offers their pages. Adding one
    /// means adding a usage source that can produce the same record shape - not a plugin
    /// surface (see ADR-0030).
    /// </summary>
    public static IReadOnlyList<string> VendorIds { get; } =
    [
        ClaudeVendorId,
        CodexVendorId,
    ];

    public static bool IsKnownVendorId(string? value) =>
        value is not null && VendorIds.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// How many hourly buckets travel with the card. Twenty-four is the window the card's
    /// trend draws, and it is also the point past which the scanner stops keeping individual
    /// responses and folds them into daily totals.
    /// </summary>
    public const int TrendHours = 24;

    /// <summary>
    /// The rolling window the displayed rate is measured over. Short enough to react to a
    /// burst within a minute or two, long enough that a single large response does not make
    /// the number jump by an order of magnitude.
    /// </summary>
    public const int RateWindowMinutes = 15;

    /// <summary>
    /// The most rows a page breaks its usage down into, largest first. A longer tail is
    /// dropped rather than summed into an "other" row: the shares are stated against the
    /// page's real total, which it shows separately, so a truncated list simply does not add
    /// up to one - which is the honest reading of it.
    /// </summary>
    public const int MaxBreakdownRows = 5;

    public const int MaxModelNameLength = 128;

    /// <summary>
    /// The model name Claude Code writes for messages it synthesised itself rather than
    /// requested. They carry a usage object but were never billed, so they are excluded.
    /// </summary>
    public const string SyntheticModel = "<synthetic>";

    // --- metric identifiers ---------------------------------------------------------------

    /// <summary>Input, output and cache-creation tokens today. Cache reads are excluded:
    /// they are the cheap part, and folding them in makes every other number invisible.</summary>
    public const string TodayBilledTokens = "usage.today.billed";

    public const string TodayOutputTokens = "usage.today.output";

    /// <summary>
    /// Cache reads today, kept as its own reading rather than as a footnote on the billed
    /// total. It is an order of magnitude larger than everything else and needs a name of its
    /// own to not be misread as part of the bill - and a name is the one thing the broker
    /// cannot compose, since it has to be translated.
    /// </summary>
    public const string TodayCacheReadTokens = "usage.today.cache-read";

    public const string TodayRequests = "usage.today.requests";

    /// <summary>Cache reads over all cacheable input today.</summary>
    public const string CacheHitRate = "usage.cache.hit-rate";

    /// <summary>
    /// Every reading, in priority order - most worth seeing first.
    ///
    /// The order is load-bearing, not cosmetic: a card is a fixed number of grid rows tall and
    /// clips what does not fit, so the smaller sizes show a prefix of this list. Reordering it
    /// changes what a small card drops.
    /// </summary>
    public static IReadOnlyList<string> MetricIds { get; } =
    [
        TodayBilledTokens,
        TodayCacheReadTokens,
        TodayRequests,
        CacheHitRate,
        TodayOutputTokens,
    ];

    public static IReadOnlyList<string> Methods { get; } =
    [
        SettingsGetMethod,
        SettingsSaveMethod,
    ];

    public static bool IsValidInstanceId(string? value) =>
        CardsContract.IsValidIdentifier(value, MaxInstanceIdLength);

    /// <summary>
    /// Validates a stored or requested vendor selection. An empty list is legal and means the
    /// card is switched off entirely, which is a state the user can choose.
    /// </summary>
    public static bool IsValidVendorList(IReadOnlyList<string>? vendors)
    {
        if (vendors is null || vendors.Count > VendorIds.Count)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string vendor in vendors)
        {
            if (!IsKnownVendorId(vendor) || !seen.Add(vendor))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsKnownMetricId(string? value) =>
        value is not null && MetricIds.Contains(value, StringComparer.Ordinal);
}

public static class TokenUsageMetricStatus
{
    public const string Ready = "ready";

    /// <summary>
    /// Nothing to report: no transcripts were found, or none fall inside today.
    ///
    /// There is deliberately no "pending" beside this. An idle rate is a real reading of zero
    /// and says something useful, and the one genuinely indeterminate moment - before the
    /// first scan finishes - is already the card's own Loading state rather than a per-metric
    /// status.
    /// </summary>
    public const string Empty = "empty";
}

/// <summary>
/// The card snapshot payload: one page per view the card can show. Page 0 is always the
/// overview; the rest are the enabled vendors, in contract order.
///
/// Pages rather than one flat reading because the vendors are not interchangeable - Codex
/// reports real quota windows and Claude has no equivalent field - and a single merged view
/// would either drop that or imply it covers both.
///
/// Every displayed string is composed here: the broker owns units, rounding and thousands
/// separators so the panel binds text instead of reaching into numbers. Metric names are the
/// exception - those are localized, and the panel owns its resources.
/// </summary>
public sealed record TokenUsageCardPayloadDto
{
    public IReadOnlyList<TokenUsagePageDto> Pages { get; init; } =
        Array.Empty<TokenUsagePageDto>();

    public string SampledAtUtc { get; init; } = string.Empty;
}

public sealed record TokenUsagePageDto
{
    /// <summary><see cref="TokenUsageContract.OverviewPageId"/> or a vendor id.</summary>
    public string PageId { get; init; } = string.Empty;

    public IReadOnlyList<TokenUsageMetricDto> Metrics { get; init; } =
        Array.Empty<TokenUsageMetricDto>();

    /// <summary>
    /// Hourly billed-token totals, oldest first, normalised to 0..1 against this page's own
    /// peak. Normalised here for the same reason the text is formatted here: the card has no
    /// ceiling to draw against and would have to invent one.
    /// </summary>
    public IReadOnlyList<double> Trend { get; init; } = Array.Empty<double>();

    /// <summary>
    /// What this page splits its usage by: vendors on the overview, models on a vendor page.
    /// </summary>
    public IReadOnlyList<TokenUsageBreakdownDto> Breakdown { get; init; } =
        Array.Empty<TokenUsageBreakdownDto>();

    /// <summary>
    /// Today's usage costed at the vendors' published list prices, already composed - for
    /// example "$12.34". Empty when nothing on this page could be priced.
    ///
    /// This is an estimate and the card says so. Both tools are normally used on a
    /// subscription, where the per-token rate is not what the user actually pays.
    /// </summary>
    public string CostText { get; init; } = string.Empty;

    /// <summary>
    /// How many of this page's models have no verified price in this build. Their tokens are
    /// in the totals; their cost is not, and the card has to say so rather than presenting a
    /// figure that quietly omits them.
    /// </summary>
    public int UnpricedModelCount { get; init; }

    public bool HasCost => CostText.Length > 0;
}

public sealed record TokenUsageMetricDto
{
    public string MetricId { get; init; } = string.Empty;

    public string Status { get; init; } = TokenUsageMetricStatus.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    /// <summary>
    /// 0..1 for the card's meter, or null where there is no honest full scale - a token count
    /// has no ceiling, a hit rate does.
    /// </summary>
    public double? Ratio { get; init; }
}

/// <summary>
/// One slice of a page's usage. The label is a raw identifier - a vendor id or a model name -
/// and is not translated: inventing a display name for a model this build has never heard of
/// would be worse than showing what the session actually recorded.
/// </summary>
public sealed record TokenUsageBreakdownDto
{
    public string Label { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    /// <summary>This slice's share of the page's billed tokens, 0..1.</summary>
    public double Ratio { get; init; }
}

public sealed record TokenUsageSettingsGetRequest
{
    public string? InstanceId { get; init; }
}

public sealed record TokenUsageSettingsGetResponse
{
    public TokenUsageSettingsDto Settings { get; init; } = new();

    /// <summary>
    /// Which vendors this machine actually has data for right now. It rides on the response
    /// rather than in the stored settings because it is not stored state: a vendor can be
    /// installed or removed between two openings of the dialog, and a list persisted at save
    /// time would describe the machine as it used to be.
    /// </summary>
    public IReadOnlyList<TokenUsageVendorStatusDto> Vendors { get; init; } =
        Array.Empty<TokenUsageVendorStatusDto>();
}

public sealed record TokenUsageVendorStatusDto
{
    public string VendorId { get; init; } = string.Empty;

    /// <summary>False when the vendor's session directory does not exist on this machine.</summary>
    public bool IsAvailable { get; init; }
}

public sealed record TokenUsageSettingsSaveRequest
{
    public Guid ClientOperationId { get; init; }

    public string? InstanceId { get; init; }

    public IReadOnlyList<string>? EnabledVendors { get; init; }

    /// <summary>
    /// Whether the broker may fetch the public price list once a day. Null leaves the stored
    /// value alone, so a client built before this field saves exactly what it did before.
    /// </summary>
    public bool? SyncPricing { get; init; }

    public int ExpectedRevision { get; init; }
}

public sealed record TokenUsageSettingsSaveResponse
{
    public Guid ClientOperationId { get; init; }

    public TokenUsageSettingsDto Settings { get; init; } = new();
}

public sealed record TokenUsageSettingsDto
{
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// Which vendors are counted, in contract order. Everything known is enabled by default:
    /// the card's job is to say what was spent, and a vendor silently left out of a total is
    /// the one failure mode that cannot be noticed by looking at it.
    /// </summary>
    public IReadOnlyList<string> EnabledVendors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the broker fetches the public price list once a day (ADR-0035). On by
    /// default: a stale price is the one error in the cost figure that does not show, and
    /// the fetch carries nothing about this machine.
    /// </summary>
    public bool SyncPricing { get; init; } = true;

    /// <summary>
    /// When the synced price list was last fetched or confirmed unchanged; empty when the
    /// broker has never reached the feed. Not stored settings state - it rides along so the
    /// settings page can say how fresh the prices are.
    /// </summary>
    public string PricingSyncedAtUtc { get; init; } = string.Empty;

    public int Revision { get; init; }

    public string UpdatedAtUtc { get; init; } = string.Empty;
}
