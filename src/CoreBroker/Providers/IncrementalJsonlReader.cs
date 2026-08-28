using System.Buffers;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Receives complete lines from <see cref="IncrementalJsonlReader"/>.
///
/// <see cref="BeginFile"/> exists for the vendors whose accounting is stateful across lines:
/// a source that derives increments from a running total has to know when a file is being
/// re-read from the start, or it would carry a total from content that no longer exists.
/// </summary>
internal interface IJsonlLineConsumer
{
    void BeginFile(string path, bool restartedFromStart);

    void Consume(ReadOnlySpan<byte> line);
}

/// <summary>
/// Walks a tree of append-only JSONL files and hands out the lines that are new since the last
/// pass. Both usage sources share it because the hard parts are identical regardless of what a
/// line means.
///
/// Those hard parts: the files are being written by a live process, so a share mode that
/// tolerates the writer is mandatory and the watermark may only advance past a *complete* line;
/// a file that got shorter was truncated or replaced, so its watermark points into content that
/// no longer exists and it has to be re-read whole; and a single line has a hard size cap so a
/// corrupted file cannot make the broker allocate without bound.
/// </summary>
internal sealed class IncrementalJsonlReader
{
    /// <summary>
    /// A line longer than this is treated as unparseable and skipped. Real lines run to tens of
    /// kilobytes; the cap is the same idea as the IPC frame limit.
    /// </summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private const int InitialBufferBytes = 64 * 1024;

    private readonly string _searchPattern;
    private readonly Dictionary<string, FileWatermark> _watermarks =
        new(StringComparer.OrdinalIgnoreCase);

    public IncrementalJsonlReader(string rootDirectory, string searchPattern = "*.jsonl")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        RootDirectory = rootDirectory;
        _searchPattern = searchPattern;
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Lines that could not be read at all since the last reset. A counter rather than a log
    /// line: the interesting case is a build where the file format moved and this climbs, and
    /// one number says that without writing anything derived from a session to disk.
    /// </summary>
    public long SkippedLineCount { get; private set; }

    /// <summary>True when the tree does not exist - the vendor is simply not installed.</summary>
    public bool RootExists => Directory.Exists(RootDirectory);

    /// <summary>
    /// Reads whatever was appended since the previous call.
    ///
    /// <paramref name="sinceUtc"/> only skips files that have never been read and were last
    /// written before it, which keeps a first scan proportional to the window rather than to
    /// the whole history. It is not a filter on the lines themselves: one file can span days.
    /// </summary>
    public void Scan(
        DateTimeOffset sinceUtc,
        IJsonlLineConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (!Directory.Exists(RootDirectory))
        {
            // Not an error: this vendor may simply never have run on this machine.
            _watermarks.Clear();
            return;
        }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in EnumerateFiles(cancellationToken))
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
                    // Nothing in this file can fall inside the window. A watermark at its
                    // current end still lets a later append be picked up incrementally.
                    _watermarks[path] = new FileWatermark(info.Length, info.LastWriteTimeUtc);
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
                startOffset = 0;
            }

            consumer.BeginFile(path, startOffset == 0);
            long consumed = ReadAppendedLines(path, startOffset, consumer, cancellationToken);
            _watermarks[path] = new FileWatermark(consumed, info.LastWriteTimeUtc);
        }

        if (_watermarks.Count != seenFiles.Count)
        {
            foreach (string missing in _watermarks.Keys
                .Where(key => !seenFiles.Contains(key))
                .ToArray())
            {
                _watermarks.Remove(missing);
            }
        }
    }

    /// <summary>Forgets every watermark so the next scan re-reads from the beginning.</summary>
    public void Reset()
    {
        _watermarks.Clear();
        SkippedLineCount = 0;
    }

    /// <summary>Counted by a source that parsed a line but could make nothing of it.</summary>
    public void NoteSkippedLine() => SkippedLineCount++;

    private string[] EnumerateFiles(CancellationToken cancellationToken)
    {
        // Enumerated eagerly: the tree is being written to while this runs, and a lazy walk
        // that trips over a directory created mid-enumeration would abandon the rest of it.
        try
        {
            return Directory.EnumerateFiles(
                RootDirectory,
                _searchPattern,
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                }).ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private long ReadAppendedLines(
        string path,
        long startOffset,
        IJsonlLineConsumer consumer,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // The vendor's own process is very likely writing this file right now, and may
                // roll it away underneath us. Anything less permissive fails on the file that
                // matters most.
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
                        ReadOnlySpan<byte> line = buffer.AsSpan(lineStart, newline - lineStart);
                        if (line.Length > 0 && line[^1] == (byte)'\r')
                        {
                            line = line[..^1];
                        }

                        if (!line.IsEmpty)
                        {
                            consumer.Consume(line);
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

            // Only complete lines count as consumed, so a line still being written is re-read
            // whole next time instead of being parsed in halves.
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

    private readonly record struct FileWatermark(long Offset, DateTime LastWriteUtc);
}
