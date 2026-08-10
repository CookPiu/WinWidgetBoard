namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// A debounced, single-note autosave coordinator for the NTE-001 service boundary. It is
/// intentionally UI-agnostic: a UI can await the returned result and surface failures while
/// retaining <see cref="InMemoryDraft"/> for retry.
/// </summary>
public sealed class NoteAutosaveCoordinator : IAsyncDisposable
{
    private readonly NoteRepository _repository;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();

    private CancellationTokenSource? _pendingCancellation;
    private TaskCompletionSource<NoteAutosaveResult>? _pendingCompletion;
    private Task? _pendingTask;
    private NoteAutosaveDraft? _inMemoryDraft;
    private NoteAutosaveResult? _lastResult;
    private bool _disposed;

    public NoteAutosaveCoordinator(NoteRepository repository, TimeSpan debounce)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (debounce <= TimeSpan.Zero || debounce > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounce),
                "Autosave debounce must be greater than zero and no longer than 30 seconds.");
        }

        _debounce = debounce;
    }

    public NoteAutosaveDraft? InMemoryDraft
    {
        get
        {
            lock (_gate)
            {
                return _inMemoryDraft;
            }
        }
    }

    public NoteAutosaveResult? LastResult
    {
        get
        {
            lock (_gate)
            {
                return _lastResult;
            }
        }
    }

    /// <summary>
    /// Replaces the pending draft. The previous pending task is canceled because only the
    /// latest input should be persisted.
    /// </summary>
    public Task<NoteAutosaveResult> Schedule(NoteAutosaveDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            ThrowIfDisposed();
            _pendingCancellation?.Cancel();
            _pendingCompletion?.TrySetCanceled();

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellation.Token);
            var completion = new TaskCompletionSource<NoteAutosaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inMemoryDraft = draft;
            _pendingCancellation = cancellation;
            _pendingCompletion = completion;
            _pendingTask = RunAsync(draft, cancellation, completion);
            return completion.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            _pendingCompletion?.TrySetCanceled();
            pendingTask = _pendingTask;
        }

        if (pendingTask is not null)
        {
            try
            {
                await pendingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _disposeCancellation.Dispose();
    }

    private async Task RunAsync(
        NoteAutosaveDraft draft,
        CancellationTokenSource cancellation,
        TaskCompletionSource<NoteAutosaveResult> completion)
    {
        try
        {
            await Task.Delay(_debounce, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();

            NoteRecord saved = draft.ExpectedUpdatedAtUtc is null
                ? _repository.Create(
                    draft.NoteId,
                    draft.Title,
                    draft.Body,
                    draft.BodyFormat)
                : _repository.Update(
                    draft.NoteId,
                    draft.ExpectedUpdatedAtUtc,
                    draft.Title,
                    draft.Body,
                    draft.BodyFormat);
            var result = new NoteAutosaveResult(draft, saved, null);
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCompletion, completion))
                {
                    _inMemoryDraft = null;
                    _lastResult = result;
                }
            }

            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellation.Token);
        }
        catch (Exception exception)
        {
            var result = new NoteAutosaveResult(draft, null, exception);
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCompletion, completion))
                {
                    // Keep the draft available so the UI can show a warning and retry it.
                    _inMemoryDraft = draft;
                    _lastResult = result;
                }
            }

            completion.TrySetResult(result);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCompletion, completion))
                {
                    _pendingCancellation = null;
                    _pendingCompletion = null;
                    _pendingTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed record NoteAutosaveDraft
{
    public NoteAutosaveDraft(
        string noteId,
        string title,
        string body,
        NoteBodyFormat bodyFormat,
        string? expectedUpdatedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        if (noteId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(noteId));
        }

        NoteId = noteId;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        BodyFormat = bodyFormat switch
        {
            NoteBodyFormat.PlainText or NoteBodyFormat.Markdown => bodyFormat,
            _ => throw new ArgumentOutOfRangeException(nameof(bodyFormat)),
        };
        ExpectedUpdatedAtUtc = expectedUpdatedAtUtc is null
            ? null
            : NoteRecord.FormatTimestamp(
                NoteRecord.ParseTimestamp(expectedUpdatedAtUtc, nameof(expectedUpdatedAtUtc)));
    }

    public string NoteId { get; }

    public string Title { get; }

    public string Body { get; }

    public NoteBodyFormat BodyFormat { get; }

    public string? ExpectedUpdatedAtUtc { get; }
}

public sealed record NoteAutosaveResult(
    NoteAutosaveDraft Draft,
    NoteRecord? SavedRecord,
    Exception? Error)
{
    public bool Succeeded => Error is null && SavedRecord is not null;
}
