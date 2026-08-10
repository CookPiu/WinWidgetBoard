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
    private const int MaxDraftHistory = 20;

    private readonly object _gate = new();
    private readonly INoteClient? _noteClient;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _autosaveDelay;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<NoteDraftSnapshot> _undoHistory = [];
    private readonly List<NoteDraftSnapshot> _redoHistory = [];
    private string _noteId;
    private CancellationTokenSource? _pendingSaveCancellation;
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

    public NoteEditorViewModel(
        INoteClient? noteClient,
        string noteId = DefaultNoteId,
        Action<Action>? dispatch = null,
        TimeSpan? autosaveDelay = null)
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
                return NoteMarkdownPreviewFormatter.Format(_body);
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
                    !_isLoading &&
                    !_isSaving &&
                    _pendingSaveTask is null &&
                    !_disposed;
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

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _isLoaded &&
                    !_isLoading &&
                    !_isSaving &&
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
                    !_isSaving &&
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

            if (string.Equals(_noteId, noteId, StringComparison.Ordinal))
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
            previousCancellation = _pendingSaveCancellation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _pendingSaveCancellation = cancellation;
            _pendingSaveTask = RunSaveAsync(
                _draftVersion,
                cancellation,
                skipDebounce: false);
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

        Publish(nameof(IsMarkdownPreviewVisible), nameof(CanPreviewMarkdown));
        return true;
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

        CancellationTokenSource cancellation;
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

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken);
            _pendingSaveCancellation = cancellation;
            _isSaving = true;
            _status = NoteEditorStatus.Saving;
            _errorCode = null;
            saveTask = RunSaveAsync(
                _draftVersion,
                cancellation,
                skipDebounce: true);
            _pendingSaveTask = saveTask;
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
            lock (_gate)
            {
                _isLoaded = true;
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
                _isSaving ||
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
            previousCancellation = _pendingSaveCancellation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _pendingSaveCancellation = cancellation;
            _pendingSaveTask = RunSaveAsync(
                _draftVersion,
                cancellation,
                skipDebounce: false);
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
        Task<bool>? saveTask = null;
        string propertyName;
        lock (_gate)
        {
            if (_disposed || !_isLoaded || _noteClient is null)
            {
                return;
            }

            if (isTitle)
            {
                if (string.Equals(_title, value, StringComparison.Ordinal))
                {
                    return;
                }

                AddHistoryEntry(_undoHistory, new NoteDraftSnapshot(_title, _body));
                _redoHistory.Clear();
                _title = value;
                propertyName = nameof(Title);
            }
            else
            {
                if (string.Equals(_body, value, StringComparison.Ordinal))
                {
                    return;
                }

                AddHistoryEntry(_undoHistory, new NoteDraftSnapshot(_title, _body));
                _redoHistory.Clear();
                _body = value;
                propertyName = nameof(Body);
            }

            _draftVersion++;
            _hasUnsavedChanges = true;
            _status = NoteEditorStatus.PendingSave;
            _errorCode = null;
            previousCancellation = _pendingSaveCancellation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _pendingSaveCancellation = cancellation;
            long version = _draftVersion;
            saveTask = RunSaveAsync(version, cancellation, skipDebounce: false);
            _pendingSaveTask = saveTask;
        }

        previousCancellation?.Cancel();
        Publish(
            propertyName,
            !isTitle ? nameof(MarkdownPreviewBlocks) : nameof(Title),
            nameof(Status),
            nameof(HasUnsavedChanges),
            nameof(CanLoadNote),
            nameof(CanUndo),
            nameof(CanRedo),
            nameof(CanRetrySave),
            nameof(ErrorCode));
    }

    private async Task<bool> RunSaveAsync(
        long version,
        CancellationTokenSource cancellationSource,
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
                await Task.Delay(_autosaveDelay, cancellationSource.Token)
                    .ConfigureAwait(false);
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

            foreach (string propertyName in propertyNames.Distinct(StringComparer.Ordinal))
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
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
