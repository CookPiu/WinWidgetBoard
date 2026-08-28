using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One hour of a trend, in local wall-clock time so the shape lines up with the user's day
/// rather than with UTC.
/// </summary>
public readonly record struct TokenUsageHourBucket(
    DateTimeOffset HourStartLocal,
    long BilledTokens,
    int Requests);

/// <summary>
/// One slice of a page's usage: a vendor on the overview, a model on a vendor's own page.
/// </summary>
public readonly record struct TokenUsageSlice(
    string Label,
    long BilledTokens,
    int Requests);

/// <summary>
/// One page's numbers. Formatting into display text is a separate step so the arithmetic can
/// be tested without asserting on strings.
/// </summary>
public sealed record TokenUsageAggregate
{
    public bool HasAnyRecord { get; init; }

    public long TodayBilledTokens { get; init; }

    public long TodayOutputTokens { get; init; }

    public long TodayCacheReadTokens { get; init; }

    public long TodayCacheableInputTokens { get; init; }

    public int TodayRequests { get; init; }

    /// <summary>
    /// Billed tokens per minute over the trailing window.
    ///
    /// Not shown on the card - the readings there are about spend and volume - but computed
    /// and tested, because the windowing it pins is what any later rate reading would rest on.
    /// </summary>
    public double CurrentRatePerMinute { get; init; }

    /// <summary>
    /// False when nothing at all happened in the trailing window. The rate is still a real zero
    /// and is shown as one; this only distinguishes idle from "no records exist".
    /// </summary>
    public bool HasCurrentRate { get; init; }

    /// <summary>
    /// The busiest window of *today*, measured over a window of the same length as
    /// <see cref="CurrentRatePerMinute"/>.
    ///
    /// Same length on purpose: a peak averaged over an hour against a current averaged over
    /// fifteen minutes routinely reads as "now is eight times busier than the busiest moment",
    /// which is an artefact of the two denominators rather than anything that happened.
    ///
    /// Same day on purpose too: it sits beside today's totals, and a peak drawn from the whole
    /// retained window made a vendor that did nothing today report an empty total next to a
    /// large peak - both true, and together unreadable.
    /// </summary>
    public double PeakRatePerMinute { get; init; }

    public DateTimeOffset? PeakWindowStartLocal { get; init; }

    public IReadOnlyList<TokenUsageHourBucket> Trend { get; init; } =
        Array.Empty<TokenUsageHourBucket>();

    public IReadOnlyList<TokenUsageSlice> Breakdown { get; init; } =
        Array.Empty<TokenUsageSlice>();

    /// <summary>
    /// Cache reads over all input that could have been served from cache, 0..1. Null when there
    /// is no input at all, because a rate over nothing is not zero.
    /// </summary>
    public double? CacheHitRate { get; init; }

    /// <summary>
    /// What today's usage would have cost at the vendors' published list prices, in USD.
    ///
    /// Not a bill. Both tools are normally used on a subscription, where the per-token rate is
    /// not what the user pays; this is the same "at API rates" figure ccusage and cc-switch
    /// report, and the card labels it as an estimate rather than presenting it as an invoice.
    /// </summary>
    public decimal TodayCostUsd { get; init; }

    /// <summary>
    /// The same total split the way the card reads it, so each row can carry what that row
    /// costs rather than making the reader apportion one lump sum across five figures.
    /// <see cref="TodayOutputCostUsd"/> is a part of <see cref="TodayBilledCostUsd"/>, not an
    /// addition to it - output is one of the three kinds the billed figure covers.
    /// </summary>
    public decimal TodayBilledCostUsd { get; init; }

    public decimal TodayCacheReadCostUsd { get; init; }

    public decimal TodayOutputCostUsd { get; init; }

    /// <summary>
    /// How many distinct models today's usage included that this build has no verified price
    /// for. Their tokens are counted; their cost is not, and cannot be.
    ///
    /// Reported rather than absorbed: pricing an unknown model at zero would understate the
    /// total, and an understated total is the one error that cannot be seen by looking at it.
    /// </summary>
    public int UnpricedModelCount { get; init; }

    public bool HasCost => TodayCostUsd > 0m;
}

/// <summary>One vendor's page.</summary>
public sealed record TokenUsageVendorReport(
    string VendorId,
    TokenUsageAggregate Aggregate);

/// <summary>Everything the card can page through.</summary>
public sealed record TokenUsageReport(
    TokenUsageAggregate Overview,
    IReadOnlyList<TokenUsageVendorReport> Vendors);

/// <summary>
/// Accumulates scanned responses from every enabled vendor and answers the card's questions
/// about them.
///
/// It de-duplicates, because the sources deliberately re-read whole files when they are
/// truncated and because both vendors re-state usage across lines. It also prunes: only the
/// window the card can display is kept, so memory is a function of how much was used in the
/// last day and not of how long the broker has been running.
/// </summary>
public sealed class TokenUsageAggregator
{
    /// <summary>
    /// How far back records are kept. Twenty-four hours covers the trend, and "today" reaches
    /// back a further hour at the very end of a local day; two hours of slack absorbs both that
    /// and a timezone whose offset has just changed.
    /// </summary>
    public const int RetentionHours = TokenUsageContract.TrendHours + 2;

    private readonly TimeZoneInfo _timeZone;
    private readonly List<TranscriptUsageRecord> _records = [];
    private readonly HashSet<ResponseKey> _seen = [];

    public TokenUsageAggregator(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public int RecordCount => _records.Count;

    /// <summary>
    /// Duplicate lines dropped since construction. On a healthy machine this is a large
    /// fraction of everything scanned; a sudden zero would mean the identity fields moved.
    /// </summary>
    public long DuplicateCount { get; private set; }

    /// <summary>
    /// The oldest instant a record can still be relevant to. Handed to each source so a first
    /// scan reads a day of files rather than the whole history.
    /// </summary>
    public static DateTimeOffset RetentionStart(DateTimeOffset nowUtc) =>
        nowUtc.AddHours(-RetentionHours);

    public void Ingest(IReadOnlyList<TranscriptUsageRecord> records, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(records);

        DateTimeOffset cutoff = RetentionStart(nowUtc);
        foreach (TranscriptUsageRecord record in records)
        {
            if (record.TimestampUtc < cutoff)
            {
                continue;
            }

            if (!_seen.Add(new ResponseKey(record.VendorId, record.RequestId, record.MessageId)))
            {
                DuplicateCount++;
                continue;
            }

            _records.Add(record);
        }

        Prune(cutoff);
    }

    /// <summary>
    /// Drops everything from one vendor. Called when a vendor is switched off, so its numbers
    /// leave the card immediately rather than lingering until they age out of retention.
    /// </summary>
    public void Forget(string vendorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);
        if (_records.RemoveAll(record =>
            string.Equals(record.VendorId, vendorId, StringComparison.Ordinal)) > 0)
        {
            RebuildSeen();
        }
    }

    /// <summary>
    /// Builds the overview page and one page per enabled vendor, in the order given. A vendor
    /// with no records still gets a page: "switched on and did nothing today" is a different
    /// statement from the page being absent.
    /// </summary>
    public TokenUsageReport Compute(
        DateTimeOffset nowUtc,
        IReadOnlyList<string> enabledVendors)
    {
        ArgumentNullException.ThrowIfNull(enabledVendors);

        var enabled = new HashSet<string>(enabledVendors, StringComparer.Ordinal);
        TokenUsageAggregate overview = ComputeAggregate(
            nowUtc,
            record => enabled.Contains(record.VendorId),
            record => record.VendorId);

        var vendors = new List<TokenUsageVendorReport>(enabledVendors.Count);
        foreach (string vendorId in enabledVendors)
        {
            vendors.Add(
                new TokenUsageVendorReport(
                    vendorId,
                    ComputeAggregate(
                        nowUtc,
                        record => string.Equals(
                            record.VendorId,
                            vendorId,
                            StringComparison.Ordinal),
                        record => record.Model)));
        }

        return new TokenUsageReport(overview, vendors);
    }

    private TokenUsageAggregate ComputeAggregate(
        DateTimeOffset nowUtc,
        Func<TranscriptUsageRecord, bool> include,
        Func<TranscriptUsageRecord, string> label)
    {
        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _timeZone);
        DateTimeOffset todayStartLocal = new(
            nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        DateTimeOffset currentHourLocal = new(
            nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0, nowLocal.Offset);
        DateTimeOffset rateWindowStart =
            nowUtc.AddMinutes(-TokenUsageContract.RateWindowMinutes);

        long todayBilled = 0;
        long todayOutput = 0;
        long todayCacheRead = 0;
        long todayCacheableInput = 0;
        int todayRequests = 0;
        long rateWindowBilled = 0;
        int rateWindowRequests = 0;
        bool any = false;
        decimal todayCost = 0m;
        decimal todayBilledCost = 0m;
        decimal todayCacheReadCost = 0m;
        decimal todayOutputCost = 0m;
        var unpricedModels = new HashSet<string>(StringComparer.Ordinal);

        var hourly = new long[TokenUsageContract.TrendHours];
        var hourlyRequests = new int[TokenUsageContract.TrendHours];
        // One slot per rate window across the trend span, so the peak is comparable with the
        // current rate instead of being an hourly average of it.
        int rateWindowCount =
            TokenUsageContract.TrendHours * 60 / TokenUsageContract.RateWindowMinutes;
        var rateWindows = new long[rateWindowCount];
        DateTimeOffset rateWindowsEnd = currentHourLocal.AddHours(1);
        var slices = new Dictionary<string, (long Billed, int Requests)>(StringComparer.Ordinal);

        foreach (TranscriptUsageRecord record in _records)
        {
            if (!include(record))
            {
                continue;
            }

            any = true;
            DateTimeOffset localTime = TimeZoneInfo.ConvertTime(record.TimestampUtc, _timeZone);

            if (localTime >= todayStartLocal)
            {
                todayBilled += record.BilledTokens;
                todayOutput += record.OutputTokens;
                todayCacheRead += record.CacheReadTokens;
                todayCacheableInput += record.CacheableInputTokens;
                todayRequests++;

                if (TokenUsagePricing.TryGetRate(record.Model) is { } rate)
                {
                    decimal outputCost =
                        record.OutputTokens * rate.OutputPerMillion / 1_000_000m;
                    decimal billedCost = outputCost
                        + (record.InputTokens * rate.InputPerMillion
                            + record.CacheWrite5mTokens * rate.CacheWrite5mPerMillion
                            + record.CacheWrite1hTokens * rate.CacheWrite1hPerMillion)
                        / 1_000_000m;
                    decimal cacheReadCost =
                        record.CacheReadTokens * rate.CacheReadPerMillion / 1_000_000m;

                    todayOutputCost += outputCost;
                    todayBilledCost += billedCost;
                    todayCacheReadCost += cacheReadCost;
                    todayCost += billedCost + cacheReadCost;
                }
                else
                {
                    unpricedModels.Add(record.Model);
                }

                string key = label(record);
                (long billed, int requests) = slices.TryGetValue(
                    key,
                    out (long Billed, int Requests) existing)
                    ? existing
                    : default;
                slices[key] = (billed + record.BilledTokens, requests + 1);
            }

            if (record.TimestampUtc >= rateWindowStart)
            {
                rateWindowBilled += record.BilledTokens;
                rateWindowRequests++;
            }

            // Bucketed by the hour the record falls in, not by the distance to the start of the
            // current hour: the latter floors to -1 for anything inside the current hour and
            // silently drops the most recent - and most interesting - readings.
            DateTimeOffset recordHourLocal = new(
                localTime.Year,
                localTime.Month,
                localTime.Day,
                localTime.Hour,
                0,
                0,
                localTime.Offset);
            int hoursBack = (int)Math.Round((currentHourLocal - recordHourLocal).TotalHours);
            if (hoursBack >= 0 && hoursBack < TokenUsageContract.TrendHours)
            {
                int index = TokenUsageContract.TrendHours - 1 - hoursBack;
                hourly[index] += record.BilledTokens;
                hourlyRequests[index]++;
            }

            if (localTime >= todayStartLocal)
            {
                int windowsBack = (int)Math.Floor(
                    (rateWindowsEnd - localTime).TotalMinutes /
                    TokenUsageContract.RateWindowMinutes);
                if (windowsBack >= 0 && windowsBack < rateWindowCount)
                {
                    rateWindows[rateWindowCount - 1 - windowsBack] += record.BilledTokens;
                }
            }
        }

        if (!any)
        {
            return new TokenUsageAggregate();
        }

        var trend = new TokenUsageHourBucket[TokenUsageContract.TrendHours];
        for (int i = 0; i < trend.Length; i++)
        {
            trend[i] = new TokenUsageHourBucket(
                currentHourLocal.AddHours(i - (TokenUsageContract.TrendHours - 1)),
                hourly[i],
                hourlyRequests[i]);
        }

        long peakBilled = 0;
        int peakIndex = -1;
        for (int i = 0; i < rateWindows.Length; i++)
        {
            if (rateWindows[i] > peakBilled)
            {
                peakBilled = rateWindows[i];
                peakIndex = i;
            }
        }

        return new TokenUsageAggregate
        {
            HasAnyRecord = true,
            TodayBilledTokens = todayBilled,
            TodayOutputTokens = todayOutput,
            TodayCacheReadTokens = todayCacheRead,
            TodayCacheableInputTokens = todayCacheableInput,
            TodayRequests = todayRequests,
            CurrentRatePerMinute =
                rateWindowBilled / (double)TokenUsageContract.RateWindowMinutes,
            HasCurrentRate = rateWindowRequests > 0,
            PeakRatePerMinute = peakBilled / (double)TokenUsageContract.RateWindowMinutes,
            PeakWindowStartLocal = peakIndex >= 0
                ? rateWindowsEnd.AddMinutes(
                    -(rateWindowCount - peakIndex) * TokenUsageContract.RateWindowMinutes)
                : null,
            Trend = trend,
            Breakdown = RankSlices(slices),
            CacheHitRate = todayCacheableInput > 0
                ? todayCacheRead / (double)todayCacheableInput
                : null,
            TodayCostUsd = todayCost,
            TodayBilledCostUsd = todayBilledCost,
            TodayCacheReadCostUsd = todayCacheReadCost,
            TodayOutputCostUsd = todayOutputCost,
            UnpricedModelCount = unpricedModels.Count,
        };
    }

    private static TokenUsageSlice[] RankSlices(
        Dictionary<string, (long Billed, int Requests)> slices) =>
        slices.Count == 0
            ? Array.Empty<TokenUsageSlice>()
            : slices
                .Select(pair =>
                    new TokenUsageSlice(pair.Key, pair.Value.Billed, pair.Value.Requests))
                .OrderByDescending(slice => slice.BilledTokens)
                .ThenBy(slice => slice.Label, StringComparer.Ordinal)
                .Take(TokenUsageContract.MaxBreakdownRows)
                .ToArray();

    private void Prune(DateTimeOffset cutoff)
    {
        if (_records.RemoveAll(record => record.TimestampUtc < cutoff) > 0)
        {
            RebuildSeen();
        }
    }

    private void RebuildSeen()
    {
        // The key set has to shrink with the records, or it becomes the one structure that
        // grows without bound for as long as the broker runs.
        _seen.Clear();
        foreach (TranscriptUsageRecord record in _records)
        {
            _seen.Add(new ResponseKey(record.VendorId, record.RequestId, record.MessageId));
        }
    }

    /// <summary>
    /// What makes two lines the same response. The vendor is part of it because the two id
    /// spaces are unrelated and could otherwise collide.
    /// </summary>
    private readonly record struct ResponseKey(
        string VendorId,
        string? RequestId,
        string MessageId);
}
