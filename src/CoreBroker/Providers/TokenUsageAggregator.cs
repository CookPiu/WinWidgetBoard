using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One hour of the trend, in local wall-clock time so the shape lines up with the user's day
/// rather than with UTC.
/// </summary>
public readonly record struct TokenUsageHourBucket(
    DateTimeOffset HourStartLocal,
    long BilledTokens,
    int Requests);

public readonly record struct TokenUsageModelTotal(
    string Model,
    long BilledTokens,
    int Requests);

/// <summary>
/// Everything the card needs, still as numbers. Formatting into display text is a separate
/// step so the arithmetic can be tested without asserting on strings.
/// </summary>
public sealed record TokenUsageAggregate
{
    public bool HasAnyRecord { get; init; }

    public long TodayBilledTokens { get; init; }

    public long TodayOutputTokens { get; init; }

    public long TodayCacheReadTokens { get; init; }

    public long TodayCacheableInputTokens { get; init; }

    public int TodayRequests { get; init; }

    /// <summary>Billed tokens per minute over the trailing rate window.</summary>
    public double CurrentRatePerMinute { get; init; }

    /// <summary>
    /// Null until the rate window contains anything. Distinguishes "nothing has happened for
    /// fifteen minutes" from a genuine zero, which the card shows differently.
    /// </summary>
    public bool HasCurrentRate { get; init; }

    /// <summary>
    /// The busiest rate window of the retained history, measured over a window of the same
    /// length as <see cref="CurrentRatePerMinute"/>. Same length on purpose: a peak averaged
    /// over an hour against a current averaged over fifteen minutes routinely reads as
    /// "now is eight times busier than the busiest moment", which is an artefact of the two
    /// denominators rather than anything that happened.
    /// </summary>
    public double PeakRatePerMinute { get; init; }

    public DateTimeOffset? PeakWindowStartLocal { get; init; }

    public IReadOnlyList<TokenUsageHourBucket> Trend { get; init; } =
        Array.Empty<TokenUsageHourBucket>();

    public IReadOnlyList<TokenUsageModelTotal> Models { get; init; } =
        Array.Empty<TokenUsageModelTotal>();

    /// <summary>
    /// Cache reads over all input that could have been served from cache, 0..1. Null when
    /// today has no input at all, because a rate over nothing is not zero.
    /// </summary>
    public double? CacheHitRate { get; init; }
}

/// <summary>
/// Accumulates scanned responses and answers the card's questions about them.
///
/// It de-duplicates, because the scanner deliberately re-reads whole files when they are
/// truncated and because one response is written once per content block it produced. It also
/// prunes: only the window the card can actually display is kept, so memory is a function of
/// how much was used in the last day and not of how long the broker has been running.
/// </summary>
public sealed class TokenUsageAggregator
{
    /// <summary>
    /// How far back records are kept. Twenty-four hours covers the trend, and "today" reaches
    /// back a further hour at the very end of a local day; two hours of slack absorbs both
    /// that and a timezone whose offset has just changed.
    /// </summary>
    public const int RetentionHours = TokenUsageContract.TrendHours + 2;

    private readonly TimeZoneInfo _timeZone;
    private readonly List<TranscriptUsageRecord> _records = [];
    private readonly HashSet<ResponseKey> _seen = [];

    public TokenUsageAggregator(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// Responses accepted so far, after de-duplication and pruning. Exposed for the provider's
    /// diagnostics, not for the card.
    /// </summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// Duplicate lines dropped since construction. On a healthy machine this is close to half
    /// of everything scanned; a sudden zero would mean the identity fields moved.
    /// </summary>
    public long DuplicateCount { get; private set; }

    /// <summary>
    /// The oldest instant a record can still be relevant to. The provider hands this to the
    /// scanner so a first scan reads a day of files rather than the whole history.
    /// </summary>
    public static DateTimeOffset RetentionStart(DateTimeOffset nowUtc) =>
        nowUtc.AddHours(-RetentionHours);

    public void Ingest(
        IReadOnlyList<TranscriptUsageRecord> records,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(records);

        DateTimeOffset cutoff = RetentionStart(nowUtc);
        foreach (TranscriptUsageRecord record in records)
        {
            if (record.TimestampUtc < cutoff)
            {
                // Outside the window the card can show. Its key is not remembered either:
                // nothing will ask about it again.
                continue;
            }

            if (!_seen.Add(new ResponseKey(record.RequestId, record.MessageId)))
            {
                DuplicateCount++;
                continue;
            }

            _records.Add(record);
        }

        Prune(cutoff);
    }

    public TokenUsageAggregate Compute(DateTimeOffset nowUtc)
    {
        if (_records.Count == 0)
        {
            return new TokenUsageAggregate();
        }

        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _timeZone);
        DateTimeOffset todayStartLocal = new(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            0,
            0,
            0,
            nowLocal.Offset);
        DateTimeOffset currentHourLocal = new(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            nowLocal.Hour,
            0,
            0,
            nowLocal.Offset);
        DateTimeOffset rateWindowStart =
            nowUtc.AddMinutes(-TokenUsageContract.RateWindowMinutes);

        long todayBilled = 0;
        long todayOutput = 0;
        long todayCacheRead = 0;
        long todayCacheableInput = 0;
        int todayRequests = 0;
        long rateWindowBilled = 0;
        int rateWindowRequests = 0;

        var hourly = new long[TokenUsageContract.TrendHours];
        var hourlyRequests = new int[TokenUsageContract.TrendHours];
        // One slot per rate window across the whole trend span, so the peak is comparable
        // with the current rate instead of being an hourly average of it.
        int rateWindowCount =
            TokenUsageContract.TrendHours * 60 / TokenUsageContract.RateWindowMinutes;
        var rateWindows = new long[rateWindowCount];
        DateTimeOffset rateWindowsEnd = currentHourLocal.AddHours(1);
        var modelTotals = new Dictionary<string, (long Billed, int Requests)>(
            StringComparer.Ordinal);

        foreach (TranscriptUsageRecord record in _records)
        {
            DateTimeOffset localTime = TimeZoneInfo.ConvertTime(
                record.TimestampUtc,
                _timeZone);

            if (localTime >= todayStartLocal)
            {
                todayBilled += record.BilledTokens;
                todayOutput += record.OutputTokens;
                todayCacheRead += record.CacheReadTokens;
                todayCacheableInput += record.CacheableInputTokens;
                todayRequests++;

                (long billed, int requests) = modelTotals.TryGetValue(
                    record.Model,
                    out (long Billed, int Requests) existing)
                    ? existing
                    : default;
                modelTotals[record.Model] =
                    (billed + record.BilledTokens, requests + 1);
            }

            if (record.TimestampUtc >= rateWindowStart)
            {
                rateWindowBilled += record.BilledTokens;
                rateWindowRequests++;
            }

            // Bucketed by the hour the record falls in, not by the distance to the start of
            // the current hour: the latter floors to -1 for anything inside the current hour
            // and silently drops the most recent - and most interesting - readings.
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

            int windowsBack = (int)Math.Floor(
                (rateWindowsEnd - localTime).TotalMinutes /
                TokenUsageContract.RateWindowMinutes);
            if (windowsBack >= 0 && windowsBack < rateWindowCount)
            {
                rateWindows[rateWindowCount - 1 - windowsBack] += record.BilledTokens;
            }
        }

        var trend = new TokenUsageHourBucket[TokenUsageContract.TrendHours];
        for (int i = 0; i < trend.Length; i++)
        {
            DateTimeOffset hourStart = currentHourLocal.AddHours(
                i - (TokenUsageContract.TrendHours - 1));
            trend[i] = new TokenUsageHourBucket(hourStart, hourly[i], hourlyRequests[i]);
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
            PeakRatePerMinute =
                peakBilled / (double)TokenUsageContract.RateWindowMinutes,
            PeakWindowStartLocal = peakIndex >= 0
                ? rateWindowsEnd.AddMinutes(
                    -(rateWindowCount - peakIndex) * TokenUsageContract.RateWindowMinutes)
                : null,
            Trend = trend,
            Models = RankModels(modelTotals),
            CacheHitRate = todayCacheableInput > 0
                ? todayCacheRead / (double)todayCacheableInput
                : null,
        };
    }

    private static TokenUsageModelTotal[] RankModels(
        Dictionary<string, (long Billed, int Requests)> totals)
    {
        if (totals.Count == 0)
        {
            return Array.Empty<TokenUsageModelTotal>();
        }

        return totals
            .Select(pair => new TokenUsageModelTotal(
                pair.Key,
                pair.Value.Billed,
                pair.Value.Requests))
            .OrderByDescending(model => model.BilledTokens)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .Take(TokenUsageContract.MaxModelRows)
            .ToArray();
    }

    private void Prune(DateTimeOffset cutoff)
    {
        int removed = _records.RemoveAll(record => record.TimestampUtc < cutoff);
        if (removed == 0)
        {
            return;
        }

        // The key set has to shrink with the records, or it becomes the one structure that
        // grows without bound for as long as the broker runs.
        _seen.Clear();
        foreach (TranscriptUsageRecord record in _records)
        {
            _seen.Add(new ResponseKey(record.RequestId, record.MessageId));
        }
    }

    /// <summary>
    /// What makes two lines the same response. Both halves are used: the message ID alone
    /// would merge a retried request that legitimately cost tokens twice.
    /// </summary>
    private readonly record struct ResponseKey(string? RequestId, string MessageId);
}
