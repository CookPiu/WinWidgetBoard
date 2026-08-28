using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One billed response, lifted from a vendor's own session records.
///
/// The identity pair is what makes the reading correct rather than roughly right. Both vendors
/// re-state usage across several lines, in different ways and for different reasons, and both
/// of them nearly double the total if the repeats are summed.
/// </summary>
public readonly record struct TranscriptUsageRecord(
    string VendorId,
    string? RequestId,
    string MessageId,
    string Model,
    DateTimeOffset TimestampUtc,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens)
{
    /// <summary>
    /// The part charged at the full rate. Cache reads are excluded because they are roughly an
    /// order of magnitude larger and an order of magnitude cheaper; a sum they are folded into
    /// stops tracking anything that moves.
    /// </summary>
    public long BilledTokens => InputTokens + OutputTokens + CacheCreationTokens;

    public long TotalTokens => BilledTokens + CacheReadTokens;

    /// <summary>
    /// Input that could have come from cache, which is what a hit rate is a fraction of. Cache
    /// creation counts: those tokens were sent uncached this time.
    /// </summary>
    public long CacheableInputTokens => InputTokens + CacheCreationTokens + CacheReadTokens;
}

/// <summary>
/// Quota exactly as a vendor reported it in its own records. Never estimated here.
/// </summary>
public sealed record VendorQuotaWindow(
    string WindowId,
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAtUtc);

public sealed record VendorQuotaSnapshot(
    IReadOnlyList<VendorQuotaWindow> Windows,
    string? CreditsBalance,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Reads one vendor's usage out of the session records it already writes on this machine.
/// </summary>
public interface ITokenUsageSource
{
    string VendorId { get; }

    /// <summary>False when the vendor's session directory does not exist on this machine.</summary>
    bool IsAvailable { get; }

    long SkippedLineCount { get; }

    /// <summary>Quota the vendor published, or null where it publishes none.</summary>
    VendorQuotaSnapshot? Quota { get; }

    IReadOnlyList<TranscriptUsageRecord> Scan(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>Forgets watermarks so the next scan re-reads the window from the start.</summary>
    void Reset();
}

/// <summary>
/// Claude Code writes one JSONL file per session under the user profile, and a response is
/// written once per content block it produced - thinking, text, each tool call - with every one
/// of those lines repeating the whole usage object. On the reference machine that is 47% of the
/// usage lines, so de-duplicating by (requestId, messageId) is a correctness requirement.
///
/// It publishes no quota.
/// </summary>
public sealed class ClaudeTokenUsageSource : ITokenUsageSource, IJsonlLineConsumer
{
    private readonly IncrementalJsonlReader _reader;
    private List<TranscriptUsageRecord> _pending = [];

    public ClaudeTokenUsageSource(string? rootDirectory = null)
    {
        _reader = new IncrementalJsonlReader(
            string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRootDirectory : rootDirectory);
    }

    /// <summary>
    /// Resolved from the profile directory rather than an environment variable, so a broker
    /// started with an unusual environment still reads the real location.
    /// </summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects");

    public string VendorId => TokenUsageContract.ClaudeVendorId;

    public bool IsAvailable => _reader.RootExists;

    public long SkippedLineCount => _reader.SkippedLineCount;

    /// <summary>Claude's transcripts carry no quota or rate-limit fields.</summary>
    public VendorQuotaSnapshot? Quota => null;

    public string RootDirectory => _reader.RootDirectory;

    public IReadOnlyList<TranscriptUsageRecord> Scan(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        _pending = [];
        _reader.Scan(sinceUtc, this, cancellationToken);
        return _pending;
    }

    public void Reset() => _reader.Reset();

    void IJsonlLineConsumer.BeginFile(string path, bool restartedFromStart)
    {
        // Stateless across lines: every line carries its own complete usage.
    }

    void IJsonlLineConsumer.Consume(ReadOnlySpan<byte> line)
    {
        if (TryParseLine(line, out TranscriptUsageRecord record))
        {
            _pending.Add(record);
        }
    }

    /// <summary>
    /// Extracts one record without materializing the message content. Every property that is
    /// not accounted for is skipped as a token subtree, so a conversation never becomes a
    /// string in this process.
    /// </summary>
    internal bool TryParseLine(ReadOnlySpan<byte> line, out TranscriptUsageRecord record)
    {
        record = default;
        string? requestId = null;
        string? messageId = null;
        string? model = null;
        DateTimeOffset timestamp = default;
        bool hasTimestamp = false;
        bool hasUsage = false;
        long input = 0;
        long output = 0;
        long cacheCreation = 0;
        long cacheRead = 0;

        try
        {
            var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("requestId"u8))
                {
                    reader.Read();
                    requestId = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : null;
                }
                else if (reader.ValueTextEquals("timestamp"u8))
                {
                    reader.Read();
                    hasTimestamp = reader.TokenType == JsonTokenType.String &&
                        reader.TryGetDateTimeOffset(out timestamp);
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        JsonScan.SkipValue(ref reader);
                        continue;
                    }

                    ReadMessage(
                        ref reader,
                        ref messageId,
                        ref model,
                        ref hasUsage,
                        ref input,
                        ref output,
                        ref cacheCreation,
                        ref cacheRead);
                }
                else
                {
                    reader.Read();
                    JsonScan.SkipValue(ref reader);
                }
            }
        }
        catch (JsonException)
        {
            _reader.NoteSkippedLine();
            return false;
        }

        if (!hasUsage ||
            !hasTimestamp ||
            string.IsNullOrEmpty(messageId) ||
            string.IsNullOrEmpty(model) ||
            string.Equals(model, TokenUsageContract.SyntheticModel, StringComparison.Ordinal))
        {
            // Not a billed response: a user turn, an attachment, a session marker, or a message
            // the agent synthesised locally. None of those cost tokens.
            return false;
        }

        record = new TranscriptUsageRecord(
            TokenUsageContract.ClaudeVendorId,
            requestId,
            messageId,
            JsonScan.TrimModel(model),
            timestamp.ToUniversalTime(),
            input,
            output,
            cacheCreation,
            cacheRead);
        return true;
    }

    private static void ReadMessage(
        ref Utf8JsonReader reader,
        ref string? messageId,
        ref string? model,
        ref bool hasUsage,
        ref long input,
        ref long output,
        ref long cacheCreation,
        ref long cacheRead)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                messageId = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : null;
            }
            else if (reader.ValueTextEquals("model"u8))
            {
                reader.Read();
                model = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("usage"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    JsonScan.SkipValue(ref reader);
                    continue;
                }

                hasUsage = true;
                ReadUsage(ref reader, ref input, ref output, ref cacheCreation, ref cacheRead);
            }
            else
            {
                // "content" lands here, and is skipped as a subtree rather than read.
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }
    }

    private static void ReadUsage(
        ref Utf8JsonReader reader,
        ref long input,
        ref long output,
        ref long cacheCreation,
        ref long cacheRead)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                input = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                output = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8))
            {
                cacheCreation = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8))
            {
                cacheRead = JsonScan.ReadCount(ref reader);
            }
            else
            {
                // "iterations" repeats the same four counters per internal turn and would
                // double-count if summed alongside the totals above.
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }
    }
}

/// <summary>
/// Codex writes one rollout JSONL per session, and its accounting is shaped completely
/// differently from Claude's.
///
/// Each <c>token_count</c> event carries both a <c>last_token_usage</c> for the turn and a
/// running <c>total_token_usage</c> for the session. Summing the per-turn figure is wrong:
/// the same turn is re-reported by later events, and on the reference machine that sum comes
/// out 1.4x the session total for input and 2.1x for output. The running total is the
/// authoritative number, and it was measured to be monotonic, so this source derives each
/// increment from the difference between consecutive totals. A total that goes *down* is
/// treated as a new baseline rather than a negative increment - that is a session the vendor
/// compacted, not tokens being refunded.
///
/// Codex also publishes real quota windows, which Claude does not.
/// </summary>
public sealed class CodexTokenUsageSource : ITokenUsageSource, IJsonlLineConsumer
{
    private readonly IncrementalJsonlReader _reader;
    private readonly Dictionary<string, CodexFileState> _fileStates =
        new(StringComparer.OrdinalIgnoreCase);
    private List<TranscriptUsageRecord> _pending = [];
    private CodexFileState? _current;
    private VendorQuotaSnapshot? _quota;

    public CodexTokenUsageSource(string? rootDirectory = null)
    {
        _reader = new IncrementalJsonlReader(
            string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRootDirectory : rootDirectory);
    }

    /// <summary>
    /// Only the live session tree. Archived sessions are deliberately out: they are historical
    /// records that can be re-written wholesale, and nothing in them falls inside the card's
    /// one-day window.
    /// </summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");

    public string VendorId => TokenUsageContract.CodexVendorId;

    public bool IsAvailable => _reader.RootExists;

    public long SkippedLineCount => _reader.SkippedLineCount;

    /// <summary>
    /// The most recently observed quota, or null when nothing has been read yet. Quota is
    /// read from records rather than queried, so it is exactly as old as the last session
    /// activity - which is why the snapshot carries its own observation time.
    /// </summary>
    public VendorQuotaSnapshot? Quota => _quota;

    public string RootDirectory => _reader.RootDirectory;

    public IReadOnlyList<TranscriptUsageRecord> Scan(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        _pending = [];
        _reader.Scan(sinceUtc, this, cancellationToken);
        return _pending;
    }

    public void Reset()
    {
        _reader.Reset();
        _fileStates.Clear();
        _current = null;
    }

    void IJsonlLineConsumer.BeginFile(string path, bool restartedFromStart)
    {
        if (restartedFromStart || !_fileStates.TryGetValue(path, out CodexFileState? state))
        {
            // Re-reading from the start means the running totals already counted are about to
            // arrive again; keeping the previous baseline would turn every one of them into a
            // zero increment and lose the whole file.
            state = new CodexFileState(path);
            _fileStates[path] = state;
        }

        _current = state;
    }

    void IJsonlLineConsumer.Consume(ReadOnlySpan<byte> line)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            ParseLine(line, _current);
        }
        catch (JsonException)
        {
            _reader.NoteSkippedLine();
        }
    }

    private void ParseLine(ReadOnlySpan<byte> line, CodexFileState state)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        DateTimeOffset timestamp = default;
        bool hasTimestamp = false;
        string? type = null;
        CodexPayload payload = default;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("timestamp"u8))
            {
                reader.Read();
                hasTimestamp = reader.TokenType == JsonTokenType.String &&
                    reader.TryGetDateTimeOffset(out timestamp);
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                type = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("payload"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    JsonScan.SkipValue(ref reader);
                    continue;
                }

                payload = ReadPayload(ref reader);
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        // The model is announced by turn_context and then applies to the token_count events
        // that follow it; those events name no model of their own.
        if (string.Equals(type, "turn_context", StringComparison.Ordinal) &&
            payload.Model is { Length: > 0 })
        {
            state.Model = JsonScan.TrimModel(payload.Model);
            return;
        }

        if (!payload.HasTotals || !hasTimestamp)
        {
            return;
        }

        if (payload.Quota is { } quota)
        {
            _quota = quota with { ObservedAtUtc = timestamp.ToUniversalTime() };
        }

        CodexTotals totals = payload.Totals;
        CodexTotals previous = state.PreviousTotals;
        // A drop means the session was compacted: treat this reading as a fresh baseline
        // rather than emitting negative usage.
        bool restarted = totals.Sum < previous.Sum;
        CodexTotals delta = restarted ? totals : totals.Subtract(previous);
        state.PreviousTotals = totals;

        if (delta.Sum <= 0)
        {
            // A repeat of an already-counted total. This is the common case - the same turn is
            // re-reported by later events - and dropping it is exactly what keeps the reading
            // from coming out nearly double.
            return;
        }

        state.Sequence++;
        _pending.Add(
            new TranscriptUsageRecord(
                TokenUsageContract.CodexVendorId,
                RequestId: null,
                // Codex has no message id, so identity is the file's own path plus a monotonic
                // counter of the increments taken from it. The path - not something generated
                // per read - is what makes it reproducible: a truncation re-read replays the
                // same sequence and de-duplication absorbs it. Keying on a fresh GUID instead
                // made every re-read count a second time.
                MessageId: state.Id + "#" + state.Sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                state.Model ?? "unknown",
                timestamp.ToUniversalTime(),
                delta.Input,
                delta.Output,
                delta.CacheWrite,
                delta.CacheRead));
    }

    private static CodexPayload ReadPayload(ref Utf8JsonReader reader)
    {
        var payload = new CodexPayload();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("model"u8))
            {
                reader.Read();
                payload.Model = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : null;
            }
            else if (reader.ValueTextEquals("info"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    JsonScan.SkipValue(ref reader);
                    continue;
                }

                ReadInfo(ref reader, ref payload);
            }
            else if (reader.ValueTextEquals("rate_limits"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    JsonScan.SkipValue(ref reader);
                    continue;
                }

                payload.Quota = ReadRateLimits(ref reader);
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        return payload;
    }

    private static void ReadInfo(ref Utf8JsonReader reader, ref CodexPayload payload)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("total_token_usage"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    JsonScan.SkipValue(ref reader);
                    continue;
                }

                payload.Totals = ReadTotals(ref reader);
                payload.HasTotals = true;
            }
            else
            {
                // "last_token_usage" lands here and is deliberately ignored: it is re-reported
                // across events and cannot be summed.
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }
    }

    private static CodexTotals ReadTotals(ref Utf8JsonReader reader)
    {
        var totals = default(CodexTotals);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                totals.Input = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                totals.Output = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cached_input_tokens"u8))
            {
                totals.CacheRead = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_write_input_tokens"u8))
            {
                totals.CacheWrite = JsonScan.ReadCount(ref reader);
            }
            else
            {
                // "reasoning_output_tokens" is a subset of output_tokens, not an addition.
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        // Codex reports cached input inside input_tokens; the record keeps them apart so the
        // billed figure means the same thing for both vendors.
        totals.Input = Math.Max(0, totals.Input - totals.CacheRead);
        return totals;
    }

    private static VendorQuotaSnapshot ReadRateLimits(ref Utf8JsonReader reader)
    {
        var windows = new List<VendorQuotaWindow>(2);
        string? credits = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isPrimary = reader.ValueTextEquals("primary"u8);
            bool isSecondary = reader.ValueTextEquals("secondary"u8);
            bool isCredits = reader.ValueTextEquals("credits"u8);
            if (!isPrimary && !isSecondary && !isCredits)
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
                continue;
            }

            string name = isPrimary ? "primary" : isSecondary ? "secondary" : "credits";
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                JsonScan.SkipValue(ref reader);
                continue;
            }

            if (isCredits)
            {
                credits = ReadCredits(ref reader);
                continue;
            }

            if (ReadWindow(ref reader, name) is { } window)
            {
                windows.Add(window);
            }
        }

        return new VendorQuotaSnapshot(windows, credits, default);
    }

    private static VendorQuotaWindow? ReadWindow(ref Utf8JsonReader reader, string windowId)
    {
        double used = -1d;
        int minutes = 0;
        DateTimeOffset? resetsAt = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("used_percent"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number &&
                    reader.TryGetDouble(out double value) &&
                    double.IsFinite(value))
                {
                    used = value;
                }
            }
            else if (reader.ValueTextEquals("window_minutes"u8))
            {
                minutes = (int)Math.Min(int.MaxValue, JsonScan.ReadCount(ref reader));
            }
            else if (reader.ValueTextEquals("resets_at"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number &&
                    reader.TryGetInt64(out long epochSeconds) &&
                    epochSeconds > 0)
                {
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                }
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        return used < 0d
            ? null
            : new VendorQuotaWindow(windowId, used, minutes, resetsAt);
    }

    private static string? ReadCredits(ref Utf8JsonReader reader)
    {
        string? balance = null;
        bool hasCredits = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("balance"u8))
            {
                reader.Read();
                // The vendor reports this as a string of raw precision
                // ("2927.9641200000"), which is not a balance anyone reads. Rounded to two
                // places where it parses, and passed through untouched where it does not -
                // a balance in a form this build does not recognise is still worth showing.
                balance = reader.TokenType switch
                {
                    JsonTokenType.String => FormatBalance(reader.GetString()),
                    JsonTokenType.Number => reader.GetDouble().ToString(
                        "0.##",
                        System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                };
            }
            else if (reader.ValueTextEquals("has_credits"u8))
            {
                reader.Read();
                hasCredits = reader.TokenType == JsonTokenType.True;
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        return hasCredits ? balance : null;
    }

    private static string? FormatBalance(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value) && double.IsFinite(value)
                ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : raw;

    private struct CodexPayload
    {
        public string? Model;
        public bool HasTotals;
        public CodexTotals Totals;
        public VendorQuotaSnapshot? Quota;
    }

    private struct CodexTotals
    {
        public long Input;
        public long Output;
        public long CacheWrite;
        public long CacheRead;

        public readonly long Sum => Input + Output + CacheWrite + CacheRead;

        public readonly CodexTotals Subtract(CodexTotals other) =>
            new()
            {
                Input = Math.Max(0, Input - other.Input),
                Output = Math.Max(0, Output - other.Output),
                CacheWrite = Math.Max(0, CacheWrite - other.CacheWrite),
                CacheRead = Math.Max(0, CacheRead - other.CacheRead),
            };
    }

    private sealed class CodexFileState
    {
        public CodexFileState(string path)
        {
            Id = path;
        }

        /// <summary>
        /// The file this state belongs to. Used as the identity prefix, so it has to be
        /// derived from the file rather than generated.
        /// </summary>
        public string Id { get; }

        public string? Model { get; set; }

        public CodexTotals PreviousTotals { get; set; }

        public long Sequence { get; set; }
    }
}

/// <summary>
/// The few reader moves both sources need. Kept together so "skip everything we do not
/// account for" is written once and cannot drift into two behaviours.
/// </summary>
internal static class JsonScan
{
    public static long ReadCount(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt64(out long value) &&
            value >= 0)
        {
            return value;
        }

        SkipValue(ref reader);
        return 0;
    }

    public static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    public static string TrimModel(string model) =>
        model.Length > TokenUsageContract.MaxModelNameLength
            ? model[..TokenUsageContract.MaxModelNameLength]
            : model;
}
