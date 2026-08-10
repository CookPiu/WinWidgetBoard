using System.ComponentModel;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;

namespace WinWidgetBoard.WorkspacePanel.Notes;

public enum NoteSearchStatus
{
    Unavailable,
    Idle,
    Listing,
    Searching,
    Ready,
    Empty,
    Error,
}

public sealed record NoteSearchResult(
    string NoteId,
    string Title,
    string Preview,
    string UpdatedAtUtc);

public sealed class NoteSearchViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan DefaultSearchDebounce =
        TimeSpan.FromMilliseconds(250);
    private const int MaxPreviewLength = 160;

    private readonly object _gate = new();
    private readonly INoteClient? _noteClient;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _searchDebounce;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _searchCancellation;
    private Task<bool>? _searchTask;
    private NoteSearchResult[] _results = [];
    private string _query = string.Empty;
    private string? _errorCode;
    private NoteSearchStatus _status;
    private long _queryVersion;
    private bool _disposed;

    public NoteSearchViewModel(
        INoteClient? noteClient,
        Action<Action>? dispatch = null,
        TimeSpan? searchDebounce = null)
    {
        _noteClient = noteClient;
        _dispatch = dispatch ?? (action => action());
        _searchDebounce = searchDebounce ?? DefaultSearchDebounce;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _searchDebounce,
            TimeSpan.Zero,
            nameof(searchDebounce));
        _status = noteClient is null
            ? NoteSearchStatus.Unavailable
            : NoteSearchStatus.Idle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Query
    {
        get
        {
            lock (_gate)
            {
                return _query;
            }
        }
    }

    public IReadOnlyList<NoteSearchResult> Results
    {
        get
        {
            lock (_gate)
            {
                return _results;
            }
        }
    }

    public bool HasResults
    {
        get
        {
            lock (_gate)
            {
                return _results.Length != 0;
            }
        }
    }

    public bool IsSearching
    {
        get
        {
            lock (_gate)
            {
                return _status is NoteSearchStatus.Listing or NoteSearchStatus.Searching;
            }
        }
    }

    public bool CanSearch
    {
        get
        {
            lock (_gate)
            {
                return _noteClient is not null && !_disposed;
            }
        }
    }

    public NoteSearchStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
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

    public Task<bool> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string normalizedQuery = query?.Trim() ?? string.Empty;
        return StartQueryAsync(normalizedQuery, listAll: false, cancellationToken);
    }

    public Task<bool> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return StartQueryAsync(string.Empty, listAll: true, cancellationToken);
    }

    public Task<bool> DeleteNoteAsync(
        NoteSearchResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long queryVersion;
        lock (_gate)
        {
            if (_noteClient is null ||
                _disposed ||
                _status is NoteSearchStatus.Listing or NoteSearchStatus.Searching)
            {
                return Task.FromResult(false);
            }

            queryVersion = _queryVersion;
        }

        return DeleteNoteCoreAsync(result, queryVersion, cancellationToken);
    }

    private Task<bool> StartQueryAsync(
        string normalizedQuery,
        bool listAll,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation;
        Task<bool> task;
        lock (_gate)
        {
            _queryVersion++;
            _query = normalizedQuery;
            _results = [];
            _errorCode = null;
            previousCancellation = _searchCancellation;
            _searchCancellation = null;
            _searchTask = null;

            if (normalizedQuery.Length == 0 && !listAll)
            {
                _status = _noteClient is null
                    ? NoteSearchStatus.Unavailable
                    : NoteSearchStatus.Idle;
                task = Task.FromResult(true);
            }
            else if (_noteClient is null)
            {
                _status = NoteSearchStatus.Unavailable;
                task = Task.FromResult(false);
            }
            else
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token,
                    cancellationToken);
                _searchCancellation = cancellation;
                _status = listAll
                    ? NoteSearchStatus.Listing
                    : NoteSearchStatus.Searching;
                long version = _queryVersion;
                task = RunSearchAsync(normalizedQuery, version, cancellation);
                _searchTask = task;
            }
        }

        previousCancellation?.Cancel();
        Publish(
            nameof(Query),
            nameof(Results),
            nameof(HasResults),
            nameof(IsSearching),
            nameof(Status),
            nameof(CanSearch),
            nameof(ErrorCode));
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        Task<bool>? searchTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            searchTask = _searchTask;
        }

        _searchCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        if (searchTask is not null)
        {
            try
            {
                await searchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCancellation.Dispose();
        _searchCancellation?.Dispose();
        PropertyChanged = null;
    }

    private async Task<bool> RunSearchAsync(
        string query,
        long version,
        CancellationTokenSource cancellationSource)
    {
        bool publishFinalState = false;
        try
        {
            await Task.Delay(_searchDebounce, cancellationSource.Token)
                .ConfigureAwait(false);
            IReadOnlyList<NoteDto> notes = await _noteClient!
                .SearchNotesAsync(query, cancellationSource.Token)
                .ConfigureAwait(false);
            NoteSearchResult[] results = notes
                .Select(CreateResult)
                .ToArray();

            lock (_gate)
            {
                if (_disposed || version != _queryVersion)
                {
                    return false;
                }

                _results = results;
                _status = results.Length == 0
                    ? NoteSearchStatus.Empty
                    : NoteSearchStatus.Ready;
                _errorCode = null;
                publishFinalState = true;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!_disposed && version == _queryVersion)
                {
                    _status = NoteSearchStatus.Error;
                    _errorCode = "cancelled";
                    publishFinalState = true;
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!_disposed && version == _queryVersion)
                {
                    _results = [];
                    _status = NoteSearchStatus.Error;
                    _errorCode = GetErrorCode(exception);
                    publishFinalState = true;
                }
            }

            return false;
        }
        finally
        {
            bool clearedCurrentTask = false;
            lock (_gate)
            {
                if (ReferenceEquals(_searchCancellation, cancellationSource))
                {
                    _searchCancellation = null;
                    _searchTask = null;
                    clearedCurrentTask = true;
                }
            }

            cancellationSource.Dispose();
            if (publishFinalState && clearedCurrentTask)
            {
                Publish(
                    nameof(Results),
                    nameof(HasResults),
                    nameof(IsSearching),
                    nameof(Status),
                    nameof(ErrorCode));
            }
        }
    }

    private async Task<bool> DeleteNoteCoreAsync(
        NoteSearchResult result,
        long queryVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            NoteDeleteResponse response = await _noteClient!.DeleteNoteAsync(
                new NoteDeleteRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = result.NoteId,
                    ExpectedUpdatedAtUtc = result.UpdatedAtUtc,
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.Deleted)
            {
                SetDeleteFailure("resource.not-found", queryVersion);
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (queryVersion != _queryVersion)
                {
                    return true;
                }

                _results = _results
                    .Where(item => !string.Equals(
                        item.NoteId,
                        result.NoteId,
                        StringComparison.Ordinal))
                    .ToArray();
                _status = _results.Length == 0
                    ? NoteSearchStatus.Empty
                    : NoteSearchStatus.Ready;
                _errorCode = null;
            }

            Publish(
                nameof(Results),
                nameof(HasResults),
                nameof(Status),
                nameof(ErrorCode));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetDeleteFailure("cancelled", queryVersion);
            return false;
        }
        catch (Exception exception)
        {
            SetDeleteFailure(GetErrorCode(exception), queryVersion);
            return false;
        }
    }

    private void SetDeleteFailure(string errorCode, long queryVersion)
    {
        lock (_gate)
        {
            if (_disposed || queryVersion != _queryVersion)
            {
                return;
            }

            _status = NoteSearchStatus.Error;
            _errorCode = errorCode;
        }

        Publish(nameof(Status), nameof(ErrorCode));
    }

    private static NoteSearchResult CreateResult(NoteDto note)
    {
        string title = string.IsNullOrWhiteSpace(note.Title)
            ? note.NoteId
            : note.Title.Trim();
        string source = string.IsNullOrWhiteSpace(note.Body)
            ? title
            : note.Body;
        string preview = string.Join(
            " ",
            source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length > MaxPreviewLength)
        {
            preview = preview[..(MaxPreviewLength - 1)] + "…";
        }

        return new NoteSearchResult(
            note.NoteId,
            title,
            preview,
            note.UpdatedAtUtc);
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

    private static string GetErrorCode(Exception exception) => exception switch
    {
        CoreBrokerClientException clientException => clientException.Code,
        TimeoutException => "transport.timeout",
        IOException => "transport.unavailable",
        _ => "unknown",
    };
}
