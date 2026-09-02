using System.ComponentModel;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;

namespace WinWidgetBoard.WorkspacePanel.Notes;

public enum NoteEditorStatus
{
    Unavailable,
    Loading,
    Ready,
    PendingSave,
    Saving,
    Saved,
    Error,
}

public sealed class NoteEditorViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public const string DefaultNoteId = "primary-note";

    private static readonly TimeSpan DefaultAutosaveDelay = TimeSpan.FromMilliseconds(500);
    // How long "saved" stays on the footer before the editor is simply ready again. A
    // confirmation that never leaves becomes a permanent status line, which the visual spec's
    // steady-state rule exists to prevent.
    private static readonly TimeSpan DefaultSavedStatusHold = TimeSpan.FromSeconds(3);
    private const int MaxDraftHistory = 20;
    private const string ConflictErrorCode = "conflict.notes-revision";

    private readonly object _gate = new();
    private readonly INoteClient? _noteClient;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _autosaveDelay;
    private readonly TimeSpan _savedStatusHold;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<NoteDraftSnapshot> _undoHistory = [];
    private readonly List<NoteDraftSnapshot> _redoHistory = [];
    private string _noteId;
    private CancellationTokenSource? _pendingSaveCancellation;
    private TaskCompletionSource? _pendingFlush;
    private Task<bool>? _pendingSaveTask;
    private Task<bool>? _loadTask;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private string _bodyFormat = NotesContract.PlainTextFormat;
    private string? _expectedUpdatedAtUtc;
    private string? _errorCode;
    private NoteEditorStatus _status;
    private bool _isLoading;
    private bool _isSaving;
    private bool _hasUnsavedChanges;
    private bool _isLoaded;
    private bool _isMarkdownPreviewVisible;
    private bool _disposed;
    private long _draftVersion;
    // An undo step is a run of edits to one field between two saves, not a keystroke: the
    // snapshot for the run is taken at its first edit, and the run closes when the draft is
    // persisted or the other field is touched. Per-keystroke steps made the 20-step history
    // worth about four words.
    private bool _draftBurstOpen;
    private bool _draftBurstIsTitle;
    // The rendered preview for the body it was rendered from. Rendering runs five regular
    // expressions over the whole body, and the getter is re-read by the binding on every
    // publish, so the result is kept until the body changes.
    private string? _previewCacheBody;
    private IReadOnlyList<NoteMarkdownPreviewBlock> _previewCache = [];

    public NoteEditorViewModel(
        INoteClient? noteClient,
        string noteId = DefaultNoteId,
        Action<Action>? dispatch = null,
        TimeSpan? autosaveDelay = null,
        TimeSpan? savedStatusHold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        _noteClient = noteClient;
        _noteId = noteId;
        _dispatch = dispatch ?? (action => action());
        _autosaveDelay = autosaveDelay ?? DefaultAutosaveDelay;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _autosaveDelay,
            TimeSpan.Zero,
            nameof(autosaveDelay));
        _savedStatusHold = savedStatusHold ?? DefaultSavedStatusHold;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _savedStatusHold,
            TimeSpan.Zero,
            nameof(savedStatusHold));
        _status = noteClient is null
            ? NoteEditorStatus.Unavailable
            : NoteEditorStatus.Loading;
        _isLoading = noteClient is not null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string NoteId
    {
        get
        {
            lock (_gate)
            {
                return _noteId;
            }
        }
    }

    public string Title
    {
        get
        {
            lock (_gate)
            {
                return _title;
            }
        }
        set => UpdateDraftValue(value, isTitle: true);
    }

    public string Body
    {
        get
        {
            lock (_gate)
            {
                return _body;
            }
        }
        set => UpdateDraftValue(value, isTitle: false);
    }

    public bool IsMarkdown
    {
        get
        {
            lock (_gate)
            {
                return IsMarkdownFormat(_bodyFormat);
            }
        }
    }

    public bool IsMarkdownPreviewVisible
    {
        get
        {
            lock (_gate)
            {
                return _isMarkdownPreviewVisible;
            }
        }
    }

    public IReadOnlyList<NoteMarkdownPreviewBlock> MarkdownPreviewBlocks
    {
        get
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_previewCacheBody, _body))
                {
                    _previewCache = NoteMarkdownPreviewFormatter.Format(_body);
                    _previewCacheBody = _body;
                }

                return _previewCache;
            }
        }
    }

    public NoteEditorStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool CanChangeMarkdownMode
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    !_isLoading &&
                    !_isSaving &&
                    !_disposed;
            }
        }
    }

    public bool CanPreviewMarkdown
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    IsMarkdownFormat(_bodyFormat) &&
                    !_isLoading &&
                    !_disposed;
            }
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (_gate)
            {
                return _isLoading;
            }
        }
    }

    public bool IsSaving
    {
        get
        {
            lock (_gate)
            {
                return _isSaving;
            }
        }
    }

    public bool HasUnsavedChanges
    {
        get
        {
            lock (_gate)
            {
                return _hasUnsavedChanges;
            }
        }
    }

    public bool CanRetrySave
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    _status == NoteEditorStatus.Error &&
                    _hasUnsavedChanges &&
                    !IsConflict(_errorCode) &&
                    !_isLoading &&
                    !_isSaving &&
                    _pendingSaveTask is null &&
                    !_disposed;
            }
        }
    }

    /// <summary>
    /// Whether fetching the note again is the way out of the current error. A retry cannot fix
    /// a load that failed (there is nothing to retry) or a revision conflict (the stored note
    /// moved on, so the retained draft can never be accepted as written); both need the stored
    /// copy back. A failed save that a retry could still land is not offered a reload, which
    /// would throw the draft away.
    /// </summary>
    public bool CanReload
    {
        get
        {
            lock (_gate)
            {
                return CanReloadLocked();
            }
        }
    }

    public bool CanLoadNote
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    !_isLoading &&
                    !_isSaving &&
                    !_hasUnsavedChanges &&
                    !_disposed;
            }
        }
    }

    public bool CanDelete
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    !_isLoading &&
                    !_isSaving &&
                    !_hasUnsavedChanges &&
                    _pendingSaveTask is null &&
                    !string.IsNullOrWhiteSpace(_expectedUpdatedAtUtc) &&
                    !_disposed;
            }
        }
    }

    // Undo does not wait for a save in flight, and typing never did: an edit while saving
    // simply bumps the draft version and the save that lands is ignored as stale. Gating the
    // buttons on the save unloaded and re-created them on every half-second autosave.
    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _isLoaded &&
                    !_isLoading &&
                    _undoHistory.Count != 0 &&
                    !_disposed;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _isLoaded &&
                    !_isLoading &&
                    _redoHistory.Count != 0 &&
                    !_disposed;
            }
        }
    }

    public bool CanEdit
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null &&
                    _isLoaded &&
                    _status != NoteEditorStatus.Unavailable &&
                    !_isLoading &&
                    !_disposed;
            }
        }
    }

    public string? ErrorCode
    {
        get
        {
            lock (_gate)
            {
                return _errorCode;
            }
        }
    }

    public Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<bool>? existing;
        lock (_gate)
        {
            existing = _loadTask;
        }

        if (existing is not null)
        {
            return existing;
        }

        Task<bool> created = LoadCoreAsync(cancellationToken);
        lock (_gate)
        {
            return _loadTask ??= created;
        }
    }

    public Task<bool> LoadNoteAsync(
        string noteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _isSaving ||
                _hasUnsavedChanges ||
                _disposed)
            {
                return Task.FromResult(false);
            }

            if (string.Equals(_noteId, noteId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_expectedUpdatedAtUtc))
            {
                return Task.FromResult(true);
            }

            _isLoading = true;
            _status = NoteEditorStatus.Loading;
            _errorCode = null;
        }

        Publish(
            nameof(IsLoading),
            nameof(Status),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(ErrorCode));
        return LoadNoteCoreAsync(noteId, cancellationToken);
    }

    public Task<bool> CreateNoteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string noteId = "note-" + Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _isSaving ||
                _hasUnsavedChanges ||
                _disposed)
            {
                return Task.FromResult(false);
            }

            _isLoading = true;
            _status = NoteEditorStatus.Loading;
            _errorCode = null;
        }

        Publish(
            nameof(IsLoading),
            nameof(Status),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(ErrorCode));
        return CreateNoteCoreAsync(noteId, cancellationToken);
    }

    public Task<bool> DeleteCurrentNoteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string noteId;
        string expectedUpdatedAtUtc;
        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _isSaving ||
                _hasUnsavedChanges ||
                _pendingSaveTask is not null ||
                string.IsNullOrWhiteSpace(_expectedUpdatedAtUtc) ||
                _disposed)
            {
                return Task.FromResult(false);
            }

            noteId = _noteId;
            expectedUpdatedAtUtc = _expectedUpdatedAtUtc;
            _isLoading = true;
            _status = NoteEditorStatus.Loading;
            _errorCode = null;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanDelete),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return DeleteCurrentNoteCoreAsync(
            noteId,
            expectedUpdatedAtUtc,
            cancellationToken);
    }

    public bool Undo() => TryApplyHistory(_undoHistory, _redoHistory);

    public bool Redo() => TryApplyHistory(_redoHistory, _undoHistory);

    public bool SetMarkdownMode(bool enabled)
    {
        CancellationTokenSource? previousCancellation;
        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _isSaving ||
                _disposed ||
                IsMarkdownFormat(_bodyFormat) == enabled)
            {
                return false;
            }

            _bodyFormat = enabled
                ? NotesContract.MarkdownFormat
                : NotesContract.PlainTextFormat;
            if (!enabled)
            {
                _isMarkdownPreviewVisible = false;
            }

            _draftVersion++;
            _hasUnsavedChanges = true;
            _status = NoteEditorStatus.PendingSave;
            _errorCode = null;
            previousCancellation = ScheduleSaveLocked(skipDebounce: false);
        }

        previousCancellation?.Cancel();
        Publish(
            nameof(IsMarkdown),
            nameof(IsMarkdownPreviewVisible),
            nameof(Status),
            nameof(HasUnsavedChanges),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return true;
    }

    public bool ToggleMarkdownPreview()
    {
        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                !IsMarkdownFormat(_bodyFormat) ||
                _isLoading ||
                _disposed)
            {
                return false;
            }

            _isMarkdownPreviewVisible = !_isMarkdownPreviewVisible;
        }

        // The blocks are only announced while the preview is showing, so opening it has to
        // announce them once for whatever the body became in the meantime.
        Publish(
            nameof(IsMarkdownPreviewVisible),
            nameof(MarkdownPreviewBlocks),
            nameof(CanPreviewMarkdown));
        return true;
    }

    /// <summary>
    /// Persists the pending draft now rather than after the debounce, and reports whether the
    /// editor is clean afterwards. This is what lets "new", "open" and "delete" go ahead
    /// straight after typing: the half-second wait is an autosave detail, not a state the
    /// user should have to notice. A draft whose save already failed is not retried here;
    /// that stays an explicit action.
    /// </summary>
    public Task<bool> SaveNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<bool>? pending;
        TaskCompletionSource? flush;
        lock (_gate)
        {
            if (!_hasUnsavedChanges)
            {
                return Task.FromResult(true);
            }

            pending = _pendingSaveTask;
            flush = _pendingFlush;
            if (pending is null)
            {
                return Task.FromResult(false);
            }
        }

        flush?.TrySetResult();
        return AwaitSaveAsync(pending, cancellationToken);
    }

    /// <summary>
    /// Fetches the note again and replaces whatever the editor holds; see
    /// <see cref="CanReload"/> for when that is the right move. A note that turns out to be
    /// gone keeps the draft under a fresh identity, so nothing typed is lost.
    /// </summary>
    public Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool loadedBefore;
        string noteId;
        lock (_gate)
        {
            if (!CanReloadLocked())
            {
                return Task.FromResult(false);
            }

            loadedBefore = _isLoaded;
            noteId = _noteId;
            if (loadedBefore)
            {
                _isLoading = true;
                _status = NoteEditorStatus.Loading;
                _errorCode = null;
                // Anything a straggling save might still report belongs to the draft being
                // discarded.
                _draftVersion++;
            }
        }

        if (!loadedBefore)
        {
            Task<bool> load = LoadCoreAsync(cancellationToken);
            lock (_gate)
            {
                _loadTask = load;
            }

            return load;
        }

        Publish(
            nameof(IsLoading),
            nameof(Status),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanDelete),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return ReloadCoreAsync(noteId, cancellationToken);
    }

    public void MarkUnavailable(string errorCode = "transport.unavailable")
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _isLoaded = false;
            _isLoading = false;
            _isSaving = false;
            _status = NoteEditorStatus.Unavailable;
            _errorCode = errorCode;
            _bodyFormat = NotesContract.PlainTextFormat;
            _isMarkdownPreviewVisible = false;
            _undoHistory.Clear();
            _redoHistory.Clear();
            _draftBurstOpen = false;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(IsSaving),
            nameof(IsMarkdown),
            nameof(IsMarkdownPreviewVisible),
            nameof(MarkdownPreviewBlocks),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
    }

    public Task<bool> RetrySaveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task<bool> saveTask;
        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _isSaving ||
                !_hasUnsavedChanges ||
                _status != NoteEditorStatus.Error ||
                _pendingSaveTask is not null ||
                _disposed)
            {
                return Task.FromResult(false);
            }

            _isSaving = true;
            _status = NoteEditorStatus.Saving;
            _errorCode = null;
            ScheduleSaveLocked(skipDebounce: true, cancellationToken);
            saveTask = _pendingSaveTask!;
        }

        Publish(
            nameof(Status),
            nameof(IsSaving),
            nameof(CanChangeMarkdownMode),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return saveTask;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? pending;
            lock (_gate)
            {
                pending = _pendingSaveTask;
            }

            if (pending is null)
            {
                return;
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(pending, _pendingSaveTask))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isLoading = false;
            _isSaving = false;
            pending = _pendingSaveTask;
        }

        _pendingSaveCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _saveGate.Dispose();
        _lifetimeCancellation.Dispose();
        _pendingSaveCancellation?.Dispose();
        PropertyChanged = null;
    }

    private async Task<bool> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_noteClient is null)
        {
            return false;
        }

        SetState(NoteEditorStatus.Loading, isLoading: true, errorCode: null);
        try
        {
            NoteDto? note = await _noteClient
                .GetNoteAsync(_noteId, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _title = note?.Title ?? string.Empty;
                _body = note?.Body ?? string.Empty;
                _bodyFormat = NormalizeBodyFormat(note?.BodyFormat);
                _expectedUpdatedAtUtc = note?.UpdatedAtUtc;
                _isLoaded = true;
                _isLoading = false;
                _isSaving = false;
                _hasUnsavedChanges = false;
                _isMarkdownPreviewVisible = false;
                _errorCode = null;
                _status = NoteEditorStatus.Ready;
                _undoHistory.Clear();
                _redoHistory.Clear();
                _draftBurstOpen = false;
            }

            Publish(
                nameof(Title),
                nameof(Body),
                nameof(IsMarkdown),
                nameof(IsMarkdownPreviewVisible),
                nameof(MarkdownPreviewBlocks),
                nameof(Status),
                nameof(IsLoading),
                nameof(IsSaving),
                nameof(HasUnsavedChanges),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanPreviewMarkdown),
                nameof(CanLoadNote),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(NoteEditorStatus.Error, isLoading: false, errorCode: "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            // Not loaded: the editor has nothing to build on. Letting it accept input here
            // produced a draft with no revision, and saving that tried to create a note that
            // already existed - every retry hit the same key. The way out is a reload.
            lock (_gate)
            {
                _isLoaded = false;
                _isLoading = false;
                _status = NoteEditorStatus.Error;
                _errorCode = GetErrorCode(exception);
            }

            Publish(
                nameof(Status),
                nameof(IsLoading),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanLoadNote),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(ErrorCode));
            return false;
        }
    }

    private async Task<bool> LoadNoteCoreAsync(
        string noteId,
        CancellationToken cancellationToken)
    {
        try
        {
            NoteDto? note = await _noteClient!
                .GetNoteAsync(noteId, cancellationToken)
                .ConfigureAwait(false);
            if (note is null)
            {
                return SetSelectedNoteLoadFailure("resource.not-found");
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _noteId = noteId;
                _title = note.Title;
                _body = note.Body;
                _bodyFormat = NormalizeBodyFormat(note.BodyFormat);
                _expectedUpdatedAtUtc = note.UpdatedAtUtc;
                _isLoaded = true;
                _isLoading = false;
                _isSaving = false;
                _hasUnsavedChanges = false;
                _isMarkdownPreviewVisible = false;
                _draftVersion++;
                _errorCode = null;
                _status = NoteEditorStatus.Ready;
                _undoHistory.Clear();
                _redoHistory.Clear();
                _draftBurstOpen = false;
            }

            Publish(
                nameof(NoteId),
                nameof(Title),
                nameof(Body),
                nameof(IsMarkdown),
                nameof(IsMarkdownPreviewVisible),
                nameof(MarkdownPreviewBlocks),
                nameof(Status),
                nameof(IsLoading),
                nameof(IsSaving),
                nameof(HasUnsavedChanges),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanPreviewMarkdown),
                nameof(CanLoadNote),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(CanRetrySave),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetSelectedNoteLoadFailure("cancelled");
        }
        catch (Exception exception)
        {
            return SetSelectedNoteLoadFailure(GetErrorCode(exception));
        }
    }

    private async Task<bool> CreateNoteCoreAsync(
        string noteId,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken);
        try
        {
            NoteDto created = await _noteClient!.SaveNoteAsync(
                new NoteSaveRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = noteId,
                    Title = string.Empty,
                    Body = string.Empty,
                    BodyFormat = NotesContract.PlainTextFormat,
                },
                linkedCancellation.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _noteId = created.NoteId;
                _title = created.Title;
                _body = created.Body;
                _bodyFormat = NormalizeBodyFormat(created.BodyFormat);
                _expectedUpdatedAtUtc = created.UpdatedAtUtc;
                _isLoaded = true;
                _isLoading = false;
                _isSaving = false;
                _hasUnsavedChanges = false;
                _isMarkdownPreviewVisible = false;
                _draftVersion++;
                _errorCode = null;
                _status = NoteEditorStatus.Saved;
                _undoHistory.Clear();
                _redoHistory.Clear();
                _draftBurstOpen = false;
                StartSavedSettle(_draftVersion);
            }

            Publish(
                nameof(NoteId),
                nameof(Title),
                nameof(Body),
                nameof(IsMarkdown),
                nameof(IsMarkdownPreviewVisible),
                nameof(MarkdownPreviewBlocks),
                nameof(Status),
                nameof(IsLoading),
                nameof(IsSaving),
                nameof(HasUnsavedChanges),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanPreviewMarkdown),
                nameof(CanLoadNote),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(CanRetrySave),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return SetCreateFailure("cancelled");
        }
        catch (Exception exception)
        {
            return SetCreateFailure(GetErrorCode(exception));
        }
    }

    private async Task<bool> DeleteCurrentNoteCoreAsync(
        string noteId,
        string expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken);
        try
        {
            NoteDeleteResponse deleted = await _noteClient!.DeleteNoteAsync(
                new NoteDeleteRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = noteId,
                    ExpectedUpdatedAtUtc = expectedUpdatedAtUtc,
                },
                linkedCancellation.Token).ConfigureAwait(false);
            if (!deleted.Deleted)
            {
                return SetDeleteFailure("resource.not-found");
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _noteId = DefaultNoteId;
                _title = string.Empty;
                _body = string.Empty;
                _bodyFormat = NotesContract.PlainTextFormat;
                _expectedUpdatedAtUtc = null;
                _isLoaded = true;
                _isLoading = false;
                _isSaving = false;
                _hasUnsavedChanges = false;
                _isMarkdownPreviewVisible = false;
                _draftVersion++;
                _errorCode = null;
                _status = NoteEditorStatus.Ready;
                _undoHistory.Clear();
                _redoHistory.Clear();
                _draftBurstOpen = false;
            }

            Publish(
                nameof(NoteId),
                nameof(Title),
                nameof(Body),
                nameof(IsMarkdown),
                nameof(IsMarkdownPreviewVisible),
                nameof(MarkdownPreviewBlocks),
                nameof(Status),
                nameof(IsLoading),
                nameof(IsSaving),
                nameof(HasUnsavedChanges),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanPreviewMarkdown),
                nameof(CanLoadNote),
                nameof(CanDelete),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(CanRetrySave),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return SetDeleteFailure("cancelled");
        }
        catch (Exception exception)
        {
            return SetDeleteFailure(GetErrorCode(exception));
        }
    }

    private bool SetCreateFailure(string errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _isLoading = false;
            _isSaving = false;
            _status = NoteEditorStatus.Error;
            _errorCode = errorCode;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(IsSaving),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return false;
    }

    private bool SetDeleteFailure(string errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _isLoading = false;
            _isSaving = false;
            _status = NoteEditorStatus.Error;
            _errorCode = errorCode;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(IsSaving),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanDelete),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return false;
    }

    private bool SetSelectedNoteLoadFailure(string errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _isLoading = false;
            _status = NoteEditorStatus.Ready;
            _errorCode = errorCode;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(ErrorCode));
        return false;
    }

    private bool TryApplyHistory(
        List<NoteDraftSnapshot> source,
        List<NoteDraftSnapshot> target)
    {
        CancellationTokenSource? previousCancellation;
        lock (_gate)
        {
            if (_noteClient is null ||
                !_isLoaded ||
                _isLoading ||
                _disposed ||
                source.Count == 0)
            {
                return false;
            }

            NoteDraftSnapshot snapshot = source[^1];
            source.RemoveAt(source.Count - 1);
            AddHistoryEntry(target, new NoteDraftSnapshot(_title, _body));
            _title = snapshot.Title;
            _body = snapshot.Body;
            _draftVersion++;
            _hasUnsavedChanges = true;
            _status = NoteEditorStatus.PendingSave;
            _errorCode = null;
            // Typing after an undo starts a new step; merging it into the run that was just
            // undone would make the next undo skip straight past it.
            _draftBurstOpen = false;
            previousCancellation = ScheduleSaveLocked(skipDebounce: false);
        }

        previousCancellation?.Cancel();
        Publish(
            nameof(Title),
            nameof(Body),
            nameof(MarkdownPreviewBlocks),
            nameof(Status),
            nameof(HasUnsavedChanges),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return true;
    }

    private void UpdateDraftValue(string? value, bool isTitle)
    {
        value ??= string.Empty;
        CancellationTokenSource? previousCancellation;
        string propertyName;
        bool announcePreview;
        lock (_gate)
        {
            // Not while a switch is in flight: the fields still hold the note being left,
            // so an edit landing here would be recorded against it, then thrown away when
            // the requested note arrives and bumps the draft version. The box shows the
            // arriving note a moment later either way.
            if (_disposed || !_isLoaded || _isLoading || _noteClient is null)
            {
                return;
            }

            string current = isTitle ? _title : _body;
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                return;
            }

            if (!_draftBurstOpen || _draftBurstIsTitle != isTitle)
            {
                AddHistoryEntry(_undoHistory, new NoteDraftSnapshot(_title, _body));
                _draftBurstOpen = true;
                _draftBurstIsTitle = isTitle;
            }

            _redoHistory.Clear();
            if (isTitle)
            {
                _title = value;
                propertyName = nameof(Title);
            }
            else
            {
                _body = value;
                propertyName = nameof(Body);
            }

            // Re-rendering the preview for a body nobody is looking at is pure cost; the
            // blocks are announced when the preview opens instead.
            announcePreview = !isTitle && _isMarkdownPreviewVisible;
            _draftVersion++;
            _hasUnsavedChanges = true;
            _status = NoteEditorStatus.PendingSave;
            _errorCode = null;
            previousCancellation = ScheduleSaveLocked(skipDebounce: false);
        }

        previousCancellation?.Cancel();
        Publish(
            propertyName,
            announcePreview ? nameof(MarkdownPreviewBlocks) : propertyName,
            nameof(Status),
            nameof(HasUnsavedChanges),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
    }

    /// <summary>
    /// Replaces the pending save with one for the current draft version. Runs under the
    /// gate; the caller cancels the returned source after leaving it.
    /// </summary>
    private CancellationTokenSource? ScheduleSaveLocked(
        bool skipDebounce,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? previous = _pendingSaveCancellation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            cancellationToken);
        var flush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSaveCancellation = cancellation;
        _pendingFlush = flush;
        _pendingSaveTask = RunSaveAsync(_draftVersion, cancellation, flush, skipDebounce);
        return previous;
    }

    private async Task<bool> AwaitSaveAsync(
        Task<bool> pending,
        CancellationToken cancellationToken)
    {
        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        lock (_gate)
        {
            return !_disposed && !_hasUnsavedChanges;
        }
    }

    private async Task<bool> RunSaveAsync(
        long version,
        CancellationTokenSource cancellationSource,
        TaskCompletionSource flush,
        bool skipDebounce)
    {
        bool publishErrorAfterCleanup = false;
        try
        {
            if (skipDebounce)
            {
                // Keep the task asynchronous so the pending-task slot is assigned
                // before an immediate retry can complete and clean itself up.
                await Task.Yield();
            }
            else
            {
                // The debounce, or a flush from SaveNowAsync, whichever comes first.
                await Task.WhenAny(
                        Task.Delay(_autosaveDelay, cancellationSource.Token),
                        flush.Task)
                    .ConfigureAwait(false);
                cancellationSource.Token.ThrowIfCancellationRequested();
            }

            await _saveGate.WaitAsync(cancellationSource.Token).ConfigureAwait(false);
            try
            {
                string title;
                string body;
                string bodyFormat;
                string noteId;
                string? expectedUpdatedAtUtc;
                lock (_gate)
                {
                    if (_disposed || version != _draftVersion)
                    {
                        return false;
                    }

                    title = _title;
                    body = _body;
                    bodyFormat = _bodyFormat;
                    noteId = _noteId;
                    expectedUpdatedAtUtc = _expectedUpdatedAtUtc;
                    _isSaving = true;
                    _status = NoteEditorStatus.Saving;
                    _errorCode = null;
                }

                Publish(
                    nameof(Status),
                    nameof(IsSaving),
                    nameof(CanChangeMarkdownMode),
                    nameof(CanLoadNote),
                    nameof(CanUndo),
                    nameof(CanRedo),
                    nameof(CanRetrySave),
                    nameof(ErrorCode));

                NoteDto saved = await _noteClient!.SaveNoteAsync(
                    new NoteSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        NoteId = noteId,
                        Title = title,
                        Body = body,
                        BodyFormat = bodyFormat,
                        ExpectedUpdatedAtUtc = expectedUpdatedAtUtc,
                    },
                    cancellationSource.Token).ConfigureAwait(false);

                lock (_gate)
                {
                    _expectedUpdatedAtUtc = saved.UpdatedAtUtc;
                    _isSaving = false;
                    if (version == _draftVersion && !_disposed)
                    {
                        _hasUnsavedChanges = false;
                        _status = NoteEditorStatus.Saved;
                        _errorCode = null;
                        _draftBurstOpen = false;
                        StartSavedSettle(version);
                    }
                }

                Publish(
                    nameof(Status),
                    nameof(IsSaving),
                    nameof(HasUnsavedChanges),
                    nameof(CanChangeMarkdownMode),
                    nameof(CanLoadNote),
                    nameof(CanUndo),
                    nameof(CanRedo),
                    nameof(CanRetrySave),
                    nameof(ErrorCode));
                return true;
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (version == _draftVersion && !_disposed)
                {
                    _isSaving = false;
                    _status = NoteEditorStatus.Error;
                    _errorCode = "cancelled";
                    publishErrorAfterCleanup = true;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (version == _draftVersion && !_disposed)
                {
                    _isSaving = false;
                    _status = NoteEditorStatus.Error;
                    _errorCode = GetErrorCode(exception);
                    _draftBurstOpen = false;
                    publishErrorAfterCleanup = true;
                }
            }
        }
        finally
        {
            bool clearedPendingTask = false;
            lock (_gate)
            {
                if (ReferenceEquals(_pendingSaveCancellation, cancellationSource))
                {
                    _pendingSaveCancellation = null;
                    _pendingFlush = null;
                    _pendingSaveTask = null;
                    clearedPendingTask = true;
                }
            }

            cancellationSource.Dispose();
            if (publishErrorAfterCleanup && clearedPendingTask)
            {
                Publish(
                    nameof(Status),
                    nameof(IsSaving),
                    nameof(CanChangeMarkdownMode),
                    nameof(CanLoadNote),
                    nameof(CanUndo),
                    nameof(CanRedo),
                    nameof(CanRetrySave),
                    nameof(ErrorCode));
            }
        }

        return false;
    }

    private async Task<bool> ReloadCoreAsync(
        string noteId,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation = null;
        try
        {
            NoteDto? note = await _noteClient!
                .GetNoteAsync(noteId, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (note is null)
                {
                    // The stored note is gone. The draft is all there is, so it becomes a
                    // note of its own rather than a draft that can never be saved.
                    _noteId = "note-" + Guid.NewGuid().ToString("N");
                    _expectedUpdatedAtUtc = null;
                    _isLoaded = true;
                    _isLoading = false;
                    _isSaving = false;
                    _isMarkdownPreviewVisible = false;
                    _errorCode = null;
                    _draftVersion++;
                    _undoHistory.Clear();
                    _redoHistory.Clear();
                    _draftBurstOpen = false;
                    if (_title.Length == 0 && _body.Length == 0)
                    {
                        _hasUnsavedChanges = false;
                        _status = NoteEditorStatus.Ready;
                    }
                    else
                    {
                        _hasUnsavedChanges = true;
                        _status = NoteEditorStatus.PendingSave;
                        // The save outlives the reload call that started it: the token here
                        // belongs to the reload, not to the draft.
                        previousCancellation = ScheduleSaveLocked(
                            skipDebounce: true,
                            CancellationToken.None);
                    }
                }
                else
                {
                    _title = note.Title;
                    _body = note.Body;
                    _bodyFormat = NormalizeBodyFormat(note.BodyFormat);
                    _expectedUpdatedAtUtc = note.UpdatedAtUtc;
                    _isLoaded = true;
                    _isLoading = false;
                    _isSaving = false;
                    _hasUnsavedChanges = false;
                    _isMarkdownPreviewVisible = false;
                    _draftVersion++;
                    _errorCode = null;
                    _status = NoteEditorStatus.Ready;
                    _undoHistory.Clear();
                    _redoHistory.Clear();
                    _draftBurstOpen = false;
                }
            }

            previousCancellation?.Cancel();
            Publish(
                nameof(NoteId),
                nameof(Title),
                nameof(Body),
                nameof(IsMarkdown),
                nameof(IsMarkdownPreviewVisible),
                nameof(MarkdownPreviewBlocks),
                nameof(Status),
                nameof(IsLoading),
                nameof(IsSaving),
                nameof(HasUnsavedChanges),
                nameof(CanEdit),
                nameof(CanChangeMarkdownMode),
                nameof(CanPreviewMarkdown),
                nameof(CanLoadNote),
                nameof(CanDelete),
                nameof(CanUndo),
                nameof(CanRedo),
                nameof(CanRetrySave),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetReloadFailure("cancelled");
        }
        catch (Exception exception)
        {
            return SetReloadFailure(GetErrorCode(exception));
        }
    }

    private bool SetReloadFailure(string errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _isLoading = false;
            _status = NoteEditorStatus.Error;
            _errorCode = errorCode;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanPreviewMarkdown),
            nameof(CanLoadNote),
            nameof(CanDelete),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
        return false;
    }

    private bool CanReloadLocked() =>
        _noteClient is not null &&
        !_isLoading &&
        !_isSaving &&
        _status == NoteEditorStatus.Error &&
        (!_isLoaded || !_hasUnsavedChanges || IsConflict(_errorCode)) &&
        !_disposed;

    private void StartSavedSettle(long version)
    {
        _ = SettleSavedAsync(version);
    }

    /// <summary>
    /// Lets "saved" give way to plain readiness once the user has had a moment to see it.
    /// Nothing happens if the draft moved on or the status changed in the meantime.
    /// </summary>
    private async Task SettleSavedAsync(long version)
    {
        try
        {
            await Task.Delay(_savedStatusHold, _lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed ||
                version != _draftVersion ||
                _status != NoteEditorStatus.Saved)
            {
                return;
            }

            _status = NoteEditorStatus.Ready;
        }

        Publish(nameof(Status));
    }

    private static bool IsConflict(string? errorCode) =>
        string.Equals(errorCode, ConflictErrorCode, StringComparison.Ordinal);

    private static void AddHistoryEntry(
        List<NoteDraftSnapshot> history,
        NoteDraftSnapshot snapshot)
    {
        history.Add(snapshot);
        if (history.Count > MaxDraftHistory)
        {
            history.RemoveAt(0);
        }
    }

    private void SetState(
        NoteEditorStatus status,
        bool isLoading,
        string? errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _status = status;
            _isLoading = isLoading;
            _errorCode = errorCode;
        }

        Publish(
            nameof(Status),
            nameof(IsLoading),
            nameof(CanEdit),
            nameof(CanChangeMarkdownMode),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
    }

    private void Publish(params string[] propertyNames)
    {
        _dispatch(() =>
        {
            PropertyChangedEventHandler? handler = PropertyChanged;
            if (handler is null)
            {
                return;
            }

            string[] distinctPropertyNames = propertyNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (string propertyName in distinctPropertyNames)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }

            if (Array.IndexOf(distinctPropertyNames, nameof(CanLoadNote)) >= 0 &&
                Array.IndexOf(distinctPropertyNames, nameof(CanDelete)) < 0)
            {
                handler(this, new PropertyChangedEventArgs(nameof(CanDelete)));
            }

            // Reloadability follows the status; announcing it wherever the status is
            // announced keeps the two from drifting apart.
            if (Array.IndexOf(distinctPropertyNames, nameof(Status)) >= 0 &&
                Array.IndexOf(distinctPropertyNames, nameof(CanReload)) < 0)
            {
                handler(this, new PropertyChangedEventArgs(nameof(CanReload)));
            }
        });
    }

    private sealed record NoteDraftSnapshot(string Title, string Body);

    private static string GetErrorCode(Exception exception) => exception switch
    {
        CoreBrokerClientException clientException => clientException.Code,
        TimeoutException => "transport.timeout",
        IOException => "transport.unavailable",
        _ => "unknown",
    };

    private static bool IsMarkdownFormat(string? bodyFormat) =>
        string.Equals(
            bodyFormat,
            NotesContract.MarkdownFormat,
            StringComparison.Ordinal);

    private static string NormalizeBodyFormat(string? bodyFormat) =>
        IsMarkdownFormat(bodyFormat)
            ? NotesContract.MarkdownFormat
            : NotesContract.PlainTextFormat;
}
