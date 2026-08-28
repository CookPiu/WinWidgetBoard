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
    public const int MaxInstanceIdLength = CardsContract.MaxInstanceIdLength;
    public const string DefaultInstanceId = "demo.tokenusage";

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
    /// The most models the card breaks usage down by, largest first. A longer tail is dropped
    /// rather than summed into an "other" row: the shares are stated against today's real
    /// total, which the card shows separately, so a truncated list simply does not add up to
    /// one - which is the honest reading of it.
    /// </summary>
    public const int MaxModelRows = 5;

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

    /// <summary>Billed tokens per minute over the trailing rate window.</summary>
    public const string CurrentRate = "usage.rate.current";

    /// <summary>
    /// The busiest window of the retained history, as a per-minute rate over a window the same
    /// length as <see cref="CurrentRate"/>'s so the two can be read against each other.
    ///
    /// This is deliberately not "the speed of the last response". A transcript records only
    /// the timestamp a response completed - there is no duration, latency or first-token
    /// field anywhere in it - so a per-response speed could only be approximated from the gap
    /// to the previous entry, which also contains the user's thinking time and every tool call
    /// in between. Both rates here are measured over wall-clock windows the timestamps really
    /// do delimit.
    /// </summary>
    public const string PeakRate = "usage.rate.peak";

    public static IReadOnlyList<string> MetricIds { get; } =
    [
        TodayBilledTokens,
        TodayOutputTokens,
        TodayCacheReadTokens,
        TodayRequests,
        CacheHitRate,
        CurrentRate,
        PeakRate,
    ];

    public static bool IsValidInstanceId(string? value) =>
        CardsContract.IsValidIdentifier(value, MaxInstanceIdLength);

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
/// The card snapshot payload. Every displayed string is composed here: the broker owns units,
/// rounding and thousands separators so the panel binds text instead of reaching into numbers.
/// Metric names are the exception - those are localized, and the panel owns its resources.
/// </summary>
public sealed record TokenUsageCardPayloadDto
{
    public IReadOnlyList<TokenUsageMetricDto> Metrics { get; init; } =
        Array.Empty<TokenUsageMetricDto>();

    /// <summary>
    /// Hourly billed-token totals, oldest first, normalised to 0..1 against the window's own
    /// peak. Normalised here for the same reason the text is formatted here: the card has no
    /// ceiling to draw against and would have to invent one.
    /// </summary>
    public IReadOnlyList<double> Trend { get; init; } = Array.Empty<double>();

    public IReadOnlyList<TokenUsageModelDto> Models { get; init; } =
        Array.Empty<TokenUsageModelDto>();

    public string SampledAtUtc { get; init; } = string.Empty;
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
/// One model's share of today's usage. The name is the raw model identifier: it is not
/// translated, and inventing a display name for a model this build has never heard of would
/// be worse than showing what the transcript actually recorded.
/// </summary>
public sealed record TokenUsageModelDto
{
    public string Model { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    /// <summary>This model's share of today's billed tokens, 0..1.</summary>
    public double Ratio { get; init; }
}

