using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class NoteCommandHandler
{
    private const int MaxCachedOperations = 512;

    private readonly object _gate;
    private readonly NoteRepository _noteRepository;
    private readonly Dictionary<Guid, CachedNoteOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();

    public NoteCommandHandler(NoteRepository noteRepository, object gate)
    {
        _noteRepository = noteRepository ??
            throw new ArgumentNullException(nameof(noteRepository));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public Envelope Handle(Envelope request)
    {
        if (string.Equals(request.Method, NotesContract.SaveMethod, StringComparison.Ordinal))
        {
            return HandleSave(request);
        }

        if (string.Equals(request.Method, NotesContract.GetMethod, StringComparison.Ordinal))
        {
            return HandleGet(request);
        }

        if (string.Equals(request.Method, NotesContract.SearchMethod, StringComparison.Ordinal))
        {
            return HandleSearch(request);
        }

        if (string.Equals(request.Method, NotesContract.DeleteMethod, StringComparison.Ordinal))
        {
            return HandleDelete(request);
        }

        return CoreBrokerCommandSupport.ErrorResponse(
            request,
            "resource.unavailable",
            "resource-unavailable");
    }

    private Envelope HandleSave(Envelope request)
    {
        if (!CoreBrokerCommandSupport.TryDeserializePayload(
                request.Payload,
                out NoteSaveRequest? payload) ||
            payload is null)
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        if (!CoreBrokerCommandSupport.IsValidOperationId(payload.ClientOperationId) ||
            !CoreBrokerCommandSupport.IsValidRequiredText(
                payload.NoteId,
                NotesContract.MaxNoteIdLength) ||
            !CoreBrokerCommandSupport.IsValidText(
                payload.Title,
                NotesContract.MaxTitleLength) ||
            !CoreBrokerCommandSupport.IsValidText(
                payload.Body,
                NotesContract.MaxBodyLength) ||
            !TryParseBodyFormat(payload.BodyFormat, out NoteBodyFormat bodyFormat) ||
            !IsValidOptionalTimestamp(payload.ExpectedUpdatedAtUtc))
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        var fingerprint = new NoteSaveFingerprint(
            payload.NoteId!,
            payload.Title!,
            payload.Body!,
            payload.BodyFormat!,
            payload.ExpectedUpdatedAtUtc);

        lock (_gate)
        {
            if (TryGetCachedOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out object? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? CoreBrokerCommandSupport.ErrorResponse(
                        request,
                        "validation.invalid-argument",
                        "validation")
                    : CoreBrokerCommandSupport.SuccessResponse(
                        request,
                        NotesContract.SaveMethod,
                        cachedResponse!);
            }

            try
            {
                NoteRecord saved = payload.ExpectedUpdatedAtUtc is null
                    ? _noteRepository.Create(
                        payload.NoteId!,
                        payload.Title!,
                        payload.Body!,
                        bodyFormat)
                    : _noteRepository.Update(
                        payload.NoteId!,
                        payload.ExpectedUpdatedAtUtc,
                        payload.Title!,
                        payload.Body!,
                        bodyFormat);
                var responsePayload = new NoteSaveResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Note = ToContract(saved),
                };
                CacheOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return CoreBrokerCommandSupport.SuccessResponse(
                    request,
                    NotesContract.SaveMethod,
                    responsePayload);
            }
            catch (NoteRevisionConflictException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "conflict.notes-revision",
                    "conflict");
            }
            catch (NoteNotFoundException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "resource.not-found",
                    "resource-unavailable");
            }
            catch (SqliteException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "storage.write-failed",
                    "storage");
            }
        }
    }

    private Envelope HandleGet(Envelope request)
    {
        if (!CoreBrokerCommandSupport.TryDeserializePayload(
                request.Payload,
                out NoteGetRequest? payload) ||
            payload is null)
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        if (!CoreBrokerCommandSupport.IsValidRequiredText(
                payload.NoteId,
                NotesContract.MaxNoteIdLength))
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        lock (_gate)
        {
            try
            {
                NoteRecord? note = _noteRepository.Get(payload.NoteId!);
                return note is null
                    ? CoreBrokerCommandSupport.ErrorResponse(
                        request,
                        "resource.not-found",
                        "resource-unavailable")
                    : CoreBrokerCommandSupport.SuccessResponse(
                        request,
                        NotesContract.GetMethod,
                        new NoteGetResponse { Note = ToContract(note) });
            }
            catch (SqliteException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "storage.read-failed",
                    "storage");
            }
        }
    }

    private Envelope HandleSearch(Envelope request)
    {
        if (!CoreBrokerCommandSupport.TryDeserializePayload(
                request.Payload,
                out NoteSearchRequest? payload) ||
            payload is null)
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        if (!CoreBrokerCommandSupport.IsValidText(
                payload.Query,
                NotesContract.MaxSearchLength))
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        lock (_gate)
        {
            try
            {
                NoteDto[] notes = _noteRepository
                    .Search(payload.Query!)
                    .Take(NotesContract.MaxSearchResults)
                    .Select(ToContract)
                    .ToArray();
                return CoreBrokerCommandSupport.SuccessResponse(
                    request,
                    NotesContract.SearchMethod,
                    new NoteSearchResponse { Notes = notes });
            }
            catch (SqliteException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "storage.read-failed",
                    "storage");
            }
        }
    }

    private Envelope HandleDelete(Envelope request)
    {
        if (!CoreBrokerCommandSupport.TryDeserializePayload(
                request.Payload,
                out NoteDeleteRequest? payload) ||
            payload is null)
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        if (!CoreBrokerCommandSupport.IsValidOperationId(payload.ClientOperationId) ||
            !CoreBrokerCommandSupport.IsValidRequiredText(
                payload.NoteId,
                NotesContract.MaxNoteIdLength) ||
            !CoreBrokerCommandSupport.IsValidRequiredText(
                payload.ExpectedUpdatedAtUtc,
                NotesContract.MaxTimestampLength) ||
            !IsValidOptionalTimestamp(payload.ExpectedUpdatedAtUtc))
        {
            return CoreBrokerCommandSupport.ErrorResponse(
                request,
                "validation.invalid-argument",
                "validation");
        }

        var fingerprint = new NoteDeleteFingerprint(
            payload.NoteId!,
            payload.ExpectedUpdatedAtUtc!);

        lock (_gate)
        {
            if (TryGetCachedOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out object? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? CoreBrokerCommandSupport.ErrorResponse(
                        request,
                        "validation.invalid-argument",
                        "validation")
                    : CoreBrokerCommandSupport.SuccessResponse(
                        request,
                        NotesContract.DeleteMethod,
                        cachedResponse!);
            }

            try
            {
                _noteRepository.Delete(
                    payload.NoteId!,
                    payload.ExpectedUpdatedAtUtc!);
                var responsePayload = new NoteDeleteResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Deleted = true,
                };
                CacheOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return CoreBrokerCommandSupport.SuccessResponse(
                    request,
                    NotesContract.DeleteMethod,
                    responsePayload);
            }
            catch (NoteRevisionConflictException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "conflict.notes-revision",
                    "conflict");
            }
            catch (NoteNotFoundException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "resource.not-found",
                    "resource-unavailable");
            }
            catch (SqliteException)
            {
                return CoreBrokerCommandSupport.ErrorResponse(
                    request,
                    "storage.write-failed",
                    "storage");
            }
        }
    }

    private static bool TryParseBodyFormat(
        string? value,
        out NoteBodyFormat bodyFormat)
    {
        if (string.Equals(
                value,
                NotesContract.PlainTextFormat,
                StringComparison.Ordinal))
        {
            bodyFormat = NoteBodyFormat.PlainText;
            return true;
        }

        if (string.Equals(
                value,
                NotesContract.MarkdownFormat,
                StringComparison.Ordinal))
        {
            bodyFormat = NoteBodyFormat.Markdown;
            return true;
        }

        bodyFormat = default;
        return false;
    }

    private static bool IsValidOptionalTimestamp(string? value)
    {
        if (value is null)
        {
            return true;
        }

        return value.Length <= NotesContract.MaxTimestampLength &&
            DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _);
    }

    private bool TryGetCachedOperation(
        Guid operationId,
        object fingerprint,
        out object? response,
        out bool fingerprintConflict)
    {
        if (!_cachedOperations.TryGetValue(
                operationId,
                out CachedNoteOperation? cached))
        {
            response = null;
            fingerprintConflict = false;
            return false;
        }

        response = cached.Response;
        fingerprintConflict = !Equals(cached.Fingerprint, fingerprint);
        return true;
    }

    private void CacheOperation(
        Guid operationId,
        object fingerprint,
        object response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations &&
            _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations[operationId] = new CachedNoteOperation(
            fingerprint,
            response);
        _operationOrder.Enqueue(operationId);
    }

    private static NoteDto ToContract(NoteRecord note) =>
        new()
        {
            NoteId = note.NoteId,
            Title = note.Title,
            Body = note.Body,
            BodyFormat = note.BodyFormat switch
            {
                NoteBodyFormat.PlainText => NotesContract.PlainTextFormat,
                NoteBodyFormat.Markdown => NotesContract.MarkdownFormat,
                _ => throw new ArgumentOutOfRangeException(nameof(note)),
            },
            CreatedAtUtc = note.CreatedAtUtc,
            UpdatedAtUtc = note.UpdatedAtUtc,
        };

    private sealed record CachedNoteOperation(object Fingerprint, object Response);

    private sealed record NoteSaveFingerprint(
        string NoteId,
        string Title,
        string Body,
        string BodyFormat,
        string? ExpectedUpdatedAtUtc);

    private sealed record NoteDeleteFingerprint(
        string NoteId,
        string ExpectedUpdatedAtUtc);
}
