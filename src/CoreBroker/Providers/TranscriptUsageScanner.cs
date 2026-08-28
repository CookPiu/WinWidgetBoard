using System.Buffers;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One billed response, lifted from a transcript line.
///
/// The identity pair is what makes the reading correct rather than roughly right: a single
/// response is written once per content block it produced - thinking, text, each tool call -
/// and every one of those lines repeats the whole usage object. On this machine that is 47% of
/// the lines. Summing them without de-duplicating nearly doubles the reported usage.
/// </summary>
public readonly record struct TranscriptUsageRecord(
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
    /// The part of the usage that is charged at the full rate. Cache reads are excluded
    /// because they are roughly an order of magnitude larger and an order of magnitude
    /// cheaper; a sum they are folded into stops tracking anything that moves.
    /// </summary>
    public long BilledTokens => InputTokens + OutputTokens + CacheCreationTokens;

    /// <summary>Everything the response touched, cache reads included.</summary>
    public long TotalTokens => BilledTokens + CacheReadTokens;

    /// <summary>
    /// Input that could have come from the cache, which is what a hit rate is a fraction of.
    /// Cache creation counts: those tokens were sent uncached this time.
    /// </summary>
    public long CacheableInputTokens =>
        InputTokens + CacheCreationTokens + CacheReadTokens;
}

/// <summary>
/// Reads token accounting out of the agent transcripts on this machine, incrementally.
///
/// Two constraints shape the whole class. The first is privacy: a transcript holds entire
/// conversations - source code, file paths, whatever the user pasted - so the parser walks the
/// JSON with <see cref="Utf8JsonReader"/> and skips the content array without ever turning it
/// into a string. Four counters, a model name, an identity pair and a timestamp are the only
/// things that leave this file.
///
/// The second is that the files are append-only and are being written by a live process while
/// this reads them. So the scanner keeps a byte watermark per file, opens with a share mode
/// that tolerates the writer, and only ever advances past complete lines - a half-written last
/// line is left for the next pass rather than parsed and discarded.
/// </summary>
public sealed class TranscriptUsageScanner
{
    /// <summary>
    /// A single line longer than this is treated as unparseable and skipped. Real lines run to
    /// tens of kilobytes; the cap exists so a corrupted file cannot make the broker allocate
    /// without bound, in the same spirit as the IPC frame limit.
    /// </summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private const int InitialBufferBytes = 64 * 1024;

    private readonly string _rootDirectory;
    private readonly Dictionary<string, FileWatermark> _watermarks =
        new(StringComparer.OrdinalIgnoreCase);

    public TranscriptUsageScanner(string? rootDirectory = null)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? DefaultRootDirectory
            : rootDirectory;
    }

    /// <summary>
    /// Where the agent writes its transcripts. Resolved from the profile directory rather than
    /// an environment variable so a broker started with an unusual environment still reads the
    /// real location.
    /// </summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects");

    public string RootDirectory => _rootDirectory;

    /// <summary>
    /// Lines that parsed as JSON but carried no usable usage record, since the last reset.
    /// Kept as a counter rather than logged: the interesting case is a build where the
    /// transcript format moved and this climbs, and one number says that without writing
    /// anything derived from a conversation to disk.
    /// </summary>
    public long SkippedLineCount { get; private set; }

    /// <summary>
    /// Reads whatever has been appended since the previous call.
    ///
    /// <paramref name="sinceUtc"/> only skips files that have never been read and were last
    /// written before it - a coarse filter that keeps the first scan proportional to the
    /// window rather than to the whole history. It is not a filter on the records themselves:
    /// one file can span days, and dropping records by timestamp is the aggregator's job.
    /// </summary>
    public IReadOnlyList<TranscriptUsageRecord> Scan(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            // Not an error: the agent may simply never have run on this machine. The card
            // shows its empty state rather than a failure.
            _watermarks.Clear();
            return Array.Empty<TranscriptUsageRecord>();
        }

        var records = new List<TranscriptUsageRecord>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(
            _rootDirectory,
            "*.jsonl",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seenFiles.Add(path);

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            bool known = _watermarks.TryGetValue(path, out FileWatermark watermark);
            if (!known)
            {
                if (info.LastWriteTimeUtc < sinceUtc.UtcDateTime)
                {
                    // Nothing in this file can fall inside the window. Record a watermark at
                    // its current end so a later append is still picked up incrementally.
                    _watermarks[path] = new FileWatermark(
                        info.Length,
                        info.LastWriteTimeUtc);
                    continue;
                }

                watermark = new FileWatermark(0, DateTime.MinValue);
            }
            else if (info.Length == watermark.Offset &&
                info.LastWriteTimeUtc == watermark.LastWriteUtc)
            {
                continue;
            }

            long startOffset = watermark.Offset;
            if (info.Length < startOffset)
            {
                // Shorter than when it was last read: the file was truncated or replaced, so
                // the watermark points into content that no longer exists. Re-read it whole
                // and let de-duplication drop what was already counted.
                startOffset = 0;
            }

            long consumed = ReadAppendedLines(path, startOffset, records, cancellationToken);
            _watermarks[path] = new FileWatermark(consumed, info.LastWriteTimeUtc);
        }

        if (_watermarks.Count != seenFiles.Count)
        {
            // A session file was deleted or a project directory was cleared. Dropping the
            // watermark keeps the dictionary proportional to what exists.
            foreach (string missing in _watermarks.Keys.Where(
                key => !seenFiles.Contains(key)).ToArray())
            {
                _watermarks.Remove(missing);
            }
        }

        return records;
    }

    /// <summary>
    /// Forgets every watermark so the next scan re-reads from the beginning. Used when the
    /// aggregation window is widened past what has already been read.
    /// </summary>
    public void Reset()
    {
        _watermarks.Clear();
        SkippedLineCount = 0;
    }

    private long ReadAppendedLines(
        string path,
        long startOffset,
        List<TranscriptUsageRecord> records,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // The agent is very likely writing this file right now, and may roll it away
                // underneath us. Anything less permissive fails on the file that matters most.
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: InitialBufferBytes,
                FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            return startOffset;
        }
        catch (UnauthorizedAccessException)
        {
            return startOffset;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        try
        {
            if (startOffset > 0)
            {
                stream.Seek(startOffset, SeekOrigin.Begin);
            }

            long totalRead = 0;
            int pending = 0;
            bool discardingOversizedLine = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pending == buffer.Length)
                {
                    if (buffer.Length >= MaxLineBytes)
                    {
                        // No newline within the cap: stop trying to assemble this line and
                        // resume at the next one.
                        discardingOversizedLine = true;
                        pending = 0;
                        SkippedLineCount++;
                    }
                    else
                    {
                        buffer = Grow(buffer, pending);
                    }
                }

                int read = stream.Read(buffer, pending, buffer.Length - pending);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                int available = pending + read;
                int lineStart = 0;

                while (true)
                {
                    int newline = Array.IndexOf(
                        buffer,
                        (byte)'\n',
                        lineStart,
                        available - lineStart);
                    if (newline < 0)
                    {
                        break;
                    }

                    if (discardingOversizedLine)
                    {
                        discardingOversizedLine = false;
                    }
                    else
                    {
                        ReadOnlySpan<byte> line =
                            buffer.AsSpan(lineStart, newline - lineStart);
                        if (line.Length > 0 && line[^1] == (byte)'\r')
                        {
                            line = line[..^1];
                        }

                        if (TryParseUsageLine(line, out TranscriptUsageRecord record))
                        {
                            records.Add(record);
                        }
                    }

                    lineStart = newline + 1;
                }

                pending = available - lineStart;
                if (discardingOversizedLine)
                {
                    pending = 0;
                }
                else if (pending > 0 && lineStart > 0)
                {
                    Buffer.BlockCopy(buffer, lineStart, buffer, 0, pending);
                }
            }

            // Only complete lines are counted as consumed, so a line still being written is
            // re-read whole on the next pass instead of being parsed in halves.
            return startOffset + totalRead - pending;
        }
        catch (IOException)
        {
            return startOffset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Dispose();
        }
    }

    private static byte[] Grow(byte[] buffer, int length)
    {
        int nextSize = (int)Math.Min((long)buffer.Length * 2, MaxLineBytes);
        byte[] next = ArrayPool<byte>.Shared.Rent(nextSize);
        Buffer.BlockCopy(buffer, 0, next, 0, length);
        ArrayPool<byte>.Shared.Return(buffer);
        return next;
    }

    /// <summary>
    /// Extracts one record without materializing the message content. Every property that is
    /// not one of the handful we account for is skipped as a token subtree, so a conversation
    /// never becomes a string in this process.
    /// </summary>
    internal bool TryParseUsageLine(
        ReadOnlySpan<byte> line,
        out TranscriptUsageRecord record)
    {
        record = default;
        if (line.IsEmpty)
        {
            return false;
        }

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
                        SkipValue(ref reader);
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
                    SkipValue(ref reader);
                }
            }
        }
        catch (JsonException)
        {
            SkippedLineCount++;
            return false;
        }

        if (!hasUsage ||
            !hasTimestamp ||
            string.IsNullOrEmpty(messageId) ||
            string.IsNullOrEmpty(model) ||
            string.Equals(model, TokenUsageContract.SyntheticModel, StringComparison.Ordinal))
        {
            // Not a billed response: a user turn, an attachment, a session marker, or a
            // message the agent synthesised locally. None of those cost tokens.
            return false;
        }

        if (model.Length > TokenUsageContract.MaxModelNameLength)
        {
            model = model[..TokenUsageContract.MaxModelNameLength];
        }

        record = new TranscriptUsageRecord(
            requestId,
            messageId,
            model,
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
                model = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : null;
            }
            else if (reader.ValueTextEquals("usage"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    SkipValue(ref reader);
                    continue;
                }

                hasUsage = true;
                ReadUsage(
                    ref reader,
                    ref input,
                    ref output,
                    ref cacheCreation,
                    ref cacheRead);
            }
            else
            {
                // "content" lands here, and is skipped as a subtree rather than read.
                reader.Read();
                SkipValue(ref reader);
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
                input = ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                output = ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8))
            {
                cacheCreation = ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8))
            {
                cacheRead = ReadCount(ref reader);
            }
            else
            {
                // "iterations" repeats the same four counters per internal turn and would
                // double-count if summed alongside the totals above; it is skipped with the
                // rest of the nested detail.
                reader.Read();
                SkipValue(ref reader);
            }
        }
    }

    private static long ReadCount(ref Utf8JsonReader reader)
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

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private readonly record struct FileWatermark(long Offset, DateTime LastWriteUtc);
}
