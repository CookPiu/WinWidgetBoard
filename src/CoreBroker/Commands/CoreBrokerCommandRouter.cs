using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.CoreBroker.Commands;

public sealed class CoreBrokerCommandRouter
{
    private const string SessionPingMethod = "session.ping";
    private const int MaxCachedOperations = 512;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CachedOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();
    private readonly NoteRepository? _noteRepository;
    private readonly LayoutRepository? _layoutRepository;
    private readonly CardSnapshotSubscriptionHub? _cardSnapshotSubscriptionHub;
    private readonly ProviderRefreshVisibilityRegistry? _providerVisibilityRegistry;
    private readonly WeatherProviderRuntime? _weatherProviderRuntime;
    private readonly Dictionary<Guid, CachedNoteOperation> _cachedNoteOperations = new();
    private readonly Queue<Guid> _noteOperationOrder = new();
    private readonly Dictionary<Guid, CachedLayoutOperation> _cachedLayoutOperations = new();
    private readonly Queue<Guid> _layoutOperationOrder = new();
    private readonly Dictionary<Guid, CachedWeatherSettingsOperation>
        _cachedWeatherSettingsOperations = new();
    private readonly Queue<Guid> _weatherSettingsOperationOrder = new();
    private bool _panelVisible;
    private long _visibilityRevision;
    private int _visibilityReportCount;

    public CoreBrokerCommandRouter(
        NoteRepository? noteRepository = null,
        LayoutRepository? layoutRepository = null,
        CardSnapshotSubscriptionHub? cardSnapshotSubscriptionHub = null,
        ProviderRefreshVisibilityRegistry? providerVisibilityRegistry = null,
        WeatherProviderRuntime? weatherProviderRuntime = null)
    {
        _noteRepository = noteRepository;
        _layoutRepository = layoutRepository;
        _cardSnapshotSubscriptionHub = cardSnapshotSubscriptionHub;
        _providerVisibilityRegistry = providerVisibilityRegistry;
        _weatherProviderRuntime = weatherProviderRuntime;
    }

    public bool NotesAvailable => _noteRepository is not null;

    public bool LayoutsAvailable => _layoutRepository is not null;

    public bool CardsAvailable => _cardSnapshotSubscriptionHub is not null;

    public bool WeatherSettingsAvailable => _weatherProviderRuntime is not null;

    public bool PanelVisible
    {
        get
        {
            lock (_gate)
            {
                return _panelVisible;
            }
        }
    }

    public long VisibilityRevision
    {
        get
        {
            lock (_gate)
            {
                return _visibilityRevision;
            }
        }
    }

    public int VisibilityReportCount
    {
        get
        {
            lock (_gate)
            {
                return _visibilityReportCount;
            }
        }
    }

    public Envelope Handle(Envelope request) =>
        Handle(request, Guid.Empty, out _);

    public Envelope Handle(
        Envelope request,
        Guid connectionId,
        out CardSnapshotSubscription? subscription)
    {
        subscription = null;
        if (request.MessageType != EnvelopeMessageType.Request)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (string.Equals(request.Method, SessionPingMethod, StringComparison.Ordinal))
        {
            return SuccessResponse(
                request,
                SessionPingMethod,
                new
                {
                    serverTimeUtc = DateTimeOffset.UtcNow,
                });
        }

        if (string.Equals(
                request.Method,
                CardsContract.SubscribeMethod,
                StringComparison.Ordinal))
        {
            return HandleCardsSubscribe(request, connectionId, out subscription);
        }

        if (string.Equals(
                request.Method,
                PanelVisibilityContract.Method,
                StringComparison.Ordinal))
        {
            return HandlePanelVisibilityReport(request);
        }

        if (string.Equals(request.Method, LayoutContract.GetMethod, StringComparison.Ordinal))
        {
            return HandleLayoutGet(request);
        }

        if (string.Equals(request.Method, LayoutContract.SaveMethod, StringComparison.Ordinal))
        {
            return HandleLayoutSave(request);
        }

        if (string.Equals(request.Method, NotesContract.SaveMethod, StringComparison.Ordinal))
        {
            return HandleNoteSave(request);
        }

        if (string.Equals(request.Method, NotesContract.GetMethod, StringComparison.Ordinal))
        {
            return HandleNoteGet(request);
        }

        if (string.Equals(request.Method, NotesContract.SearchMethod, StringComparison.Ordinal))
        {
            return HandleNoteSearch(request);
        }

        if (string.Equals(request.Method, NotesContract.DeleteMethod, StringComparison.Ordinal))
        {
            return HandleNoteDelete(request);
        }

        if (string.Equals(
                request.Method,
                WeatherSettingsContract.GetMethod,
                StringComparison.Ordinal))
        {
            return HandleWeatherSettingsGet(request);
        }

        if (string.Equals(
                request.Method,
                WeatherSettingsContract.SaveMethod,
                StringComparison.Ordinal))
        {
            return HandleWeatherSettingsSave(request);
        }

        return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
    }

    public void RemoveConnection(Guid connectionId)
    {
        _cardSnapshotSubscriptionHub?.Remove(connectionId);
        _providerVisibilityRegistry?.Remove(connectionId);
    }

    private Envelope HandleCardsSubscribe(
        Envelope request,
        Guid connectionId,
        out CardSnapshotSubscription? subscription)
    {
        subscription = null;
        if (_cardSnapshotSubscriptionHub is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (connectionId == Guid.Empty ||
            !TryDeserializePayload(
                request.Payload,
                out CardsSubscribeRequest? payload) ||
            payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        try
        {
            if (!_cardSnapshotSubscriptionHub.TrySubscribe(
                    connectionId,
                    payload,
                    out CardsSubscribeResponse? responsePayload,
                    out subscription) ||
                responsePayload is null ||
                subscription is null)
            {
                subscription = null;
                return ErrorResponse(request, "validation.invalid-argument", "validation");
            }

            _providerVisibilityRegistry?.Apply(connectionId, payload);

            return SuccessResponse(
                request,
                CardsContract.SubscribeMethod,
                responsePayload);
        }
        catch (ObjectDisposedException)
        {
            subscription = null;
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }
    }

    private Envelope HandleLayoutGet(Envelope request)
    {
        if (_layoutRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out LayoutGetRequest? payload) ||
            payload is null ||
            !IsValidLayoutIdentity(payload.LayoutId, payload.DisplayId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        lock (_gate)
        {
            try
            {
                LayoutRecord? layout = _layoutRepository.Get(
                    payload.LayoutId!,
                    payload.DisplayId!);
                return layout is null
                    ? ErrorResponse(request, "resource.not-found", "resource-unavailable")
                    : SuccessResponse(
                        request,
                        LayoutContract.GetMethod,
                        new LayoutGetResponse { Layout = ToContract(layout) });
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.read-failed", "storage");
            }
        }
    }

    private Envelope HandleLayoutSave(Envelope request)
    {
        if (_layoutRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out LayoutSaveRequest? payload) ||
            payload is null ||
            !IsValidLayoutIdentity(payload.LayoutId, payload.DisplayId) ||
            !IsValidOperationId(payload.ClientOperationId) ||
            payload.ExpectedRevision < 0 ||
            !TryMapLayoutItems(payload.Items, out IReadOnlyList<LayoutItemRecord>? items))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        string fingerprint = JsonSerializer.Serialize(payload, ContractJson.Options);
        lock (_gate)
        {
            if (TryGetCachedLayoutOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out LayoutSaveResponse? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(request, LayoutContract.SaveMethod, cachedResponse!);
            }

            try
            {
                LayoutRecord saved = _layoutRepository.Save(
                    payload.LayoutId!,
                    payload.DisplayId!,
                    payload.ExpectedRevision,
                    items!);
                var responsePayload = new LayoutSaveResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Layout = ToContract(saved),
                };
                CacheLayoutOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    responsePayload);
                return SuccessResponse(
                    request,
                    LayoutContract.SaveMethod,
                    responsePayload);
            }
            catch (LayoutRevisionConflictException)
            {
                return ErrorResponse(request, "conflict.layout-revision", "conflict");
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.write-failed", "storage");
            }
        }
    }

    private Envelope HandleNoteSave(Envelope request)
    {
        if (_noteRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out NoteSaveRequest? payload) || payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (!IsValidOperationId(payload.ClientOperationId) ||
            !IsValidRequiredText(payload.NoteId, NotesContract.MaxNoteIdLength) ||
            !IsValidText(payload.Title, NotesContract.MaxTitleLength) ||
            !IsValidText(payload.Body, NotesContract.MaxBodyLength) ||
            !TryParseBodyFormat(payload.BodyFormat, out NoteBodyFormat bodyFormat) ||
            !IsValidOptionalTimestamp(payload.ExpectedUpdatedAtUtc))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        var fingerprint = new NoteSaveFingerprint(
            payload.NoteId!,
            payload.Title!,
            payload.Body!,
            payload.BodyFormat!,
            payload.ExpectedUpdatedAtUtc);

        lock (_gate)
        {
            if (TryGetCachedNoteOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out object? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(request, NotesContract.SaveMethod, cachedResponse!);
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
                CacheNoteOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return SuccessResponse(request, NotesContract.SaveMethod, responsePayload);
            }
            catch (NoteRevisionConflictException)
            {
                return ErrorResponse(request, "conflict.notes-revision", "conflict");
            }
            catch (NoteNotFoundException)
            {
                return ErrorResponse(request, "resource.not-found", "resource-unavailable");
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.write-failed", "storage");
            }
        }
    }

    private Envelope HandleNoteGet(Envelope request)
    {
        if (_noteRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out NoteGetRequest? payload) || payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (!IsValidRequiredText(payload.NoteId, NotesContract.MaxNoteIdLength))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        lock (_gate)
        {
            try
            {
                NoteRecord? note = _noteRepository.Get(payload.NoteId!);
                return note is null
                    ? ErrorResponse(request, "resource.not-found", "resource-unavailable")
                    : SuccessResponse(
                        request,
                        NotesContract.GetMethod,
                        new NoteGetResponse { Note = ToContract(note) });
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.read-failed", "storage");
            }
        }
    }

    private Envelope HandleNoteSearch(Envelope request)
    {
        if (_noteRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out NoteSearchRequest? payload) || payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (!IsValidText(payload.Query, NotesContract.MaxSearchLength))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
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
                return SuccessResponse(
                    request,
                    NotesContract.SearchMethod,
                    new NoteSearchResponse { Notes = notes });
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.read-failed", "storage");
            }
        }
    }

    private Envelope HandleNoteDelete(Envelope request)
    {
        if (_noteRepository is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(request.Payload, out NoteDeleteRequest? payload) || payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (!IsValidOperationId(payload.ClientOperationId) ||
            !IsValidRequiredText(payload.NoteId, NotesContract.MaxNoteIdLength) ||
            !IsValidRequiredText(payload.ExpectedUpdatedAtUtc, NotesContract.MaxTimestampLength) ||
            !IsValidOptionalTimestamp(payload.ExpectedUpdatedAtUtc))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        var fingerprint = new NoteDeleteFingerprint(
            payload.NoteId!,
            payload.ExpectedUpdatedAtUtc!);

        lock (_gate)
        {
            if (TryGetCachedNoteOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out object? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(request, NotesContract.DeleteMethod, cachedResponse!);
            }

            try
            {
                _noteRepository.Delete(payload.NoteId!, payload.ExpectedUpdatedAtUtc!);
                var responsePayload = new NoteDeleteResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Deleted = true,
                };
                CacheNoteOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return SuccessResponse(request, NotesContract.DeleteMethod, responsePayload);
            }
            catch (NoteRevisionConflictException)
            {
                return ErrorResponse(request, "conflict.notes-revision", "conflict");
            }
            catch (NoteNotFoundException)
            {
                return ErrorResponse(request, "resource.not-found", "resource-unavailable");
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.write-failed", "storage");
            }
        }
    }

    private Envelope HandleWeatherSettingsGet(Envelope request)
    {
        if (_weatherProviderRuntime is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(
                request.Payload,
                out WeatherSettingsGetRequest? payload) ||
            payload is null ||
            !IsValidWeatherInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        lock (_gate)
        {
            try
            {
                WeatherSettingsRecord settings = _weatherProviderRuntime.GetSettings();
                return SuccessResponse(
                    request,
                    WeatherSettingsContract.GetMethod,
                    new WeatherSettingsGetResponse
                    {
                        Settings = ToContract(settings),
                    });
            }
            catch (ObjectDisposedException)
            {
                return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.read-failed", "storage");
            }
        }
    }

    private Envelope HandleWeatherSettingsSave(Envelope request)
    {
        if (_weatherProviderRuntime is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(
                request.Payload,
                out WeatherSettingsSaveRequest? payload) ||
            payload is null ||
            !IsValidOperationId(payload.ClientOperationId) ||
            !IsValidWeatherInstanceId(payload.InstanceId) ||
            !WeatherSettingsContract.TryNormalizeLabel(
                payload.Label,
                out string? normalizedLabel) ||
            normalizedLabel is null ||
            !WeatherSettingsContract.IsValidCoordinates(
                payload.Latitude,
                payload.Longitude) ||
            payload.ExpectedRevision < 0)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        var fingerprint = new WeatherSettingsSaveFingerprint(
            payload.InstanceId!,
            normalizedLabel,
            payload.Latitude,
            payload.Longitude,
            payload.ExpectedRevision);
        lock (_gate)
        {
            if (TryGetCachedWeatherSettingsOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out WeatherSettingsSaveResponse? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(
                        request,
                        WeatherSettingsContract.SaveMethod,
                        cachedResponse!);
            }

            try
            {
                WeatherSettingsRecord saved = _weatherProviderRuntime.SaveSettings(
                    payload.InstanceId!,
                    normalizedLabel,
                    payload.Latitude,
                    payload.Longitude,
                    payload.ExpectedRevision);
                var responsePayload = new WeatherSettingsSaveResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Settings = ToContract(saved),
                };
                CacheWeatherSettingsOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    responsePayload);
                return SuccessResponse(
                    request,
                    WeatherSettingsContract.SaveMethod,
                    responsePayload);
            }
            catch (WeatherSettingsRevisionConflictException)
            {
                return ErrorResponse(
                    request,
                    "conflict.weather-settings-revision",
                    "conflict");
            }
            catch (SqliteException)
            {
                return ErrorResponse(request, "storage.write-failed", "storage");
            }
            catch (ObjectDisposedException)
            {
                return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
            }
            catch (InvalidOperationException)
            {
                return ErrorResponse(request, "provider.update-failed", "provider");
            }
        }
    }

    private Envelope HandlePanelVisibilityReport(Envelope request)
    {
        if (request.Payload.ValueKind != JsonValueKind.Object ||
            !request.Payload.TryGetProperty("clientOperationId", out JsonElement operationElement) ||
            operationElement.ValueKind != JsonValueKind.String ||
            !operationElement.TryGetGuid(out Guid clientOperationId) ||
            clientOperationId == Guid.Empty ||
            !request.Payload.TryGetProperty("panelVisible", out JsonElement visibleElement) ||
            visibleElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        string? displayId = null;
        if (request.Payload.TryGetProperty("displayId", out JsonElement displayElement))
        {
            if (displayElement.ValueKind == JsonValueKind.Null)
            {
                displayId = null;
            }
            else if (displayElement.ValueKind is not JsonValueKind.String)
            {
                return ErrorResponse(request, "validation.invalid-argument", "validation");
            }
            else
            {
                displayId = displayElement.GetString();
                if (displayId?.Length > PanelVisibilityContract.MaxDisplayIdLength)
                {
                    return ErrorResponse(request, "validation.invalid-argument", "validation");
                }
            }
        }

        var report = new PanelVisibilityReportRequest
        {
            ClientOperationId = clientOperationId,
            PanelVisible = visibleElement.GetBoolean(),
            DisplayId = displayId,
        };

        lock (_gate)
        {
            if (_cachedOperations.TryGetValue(clientOperationId, out CachedOperation? cached))
            {
                if (!Matches(cached.Request, report))
                {
                    return ErrorResponse(request, "validation.invalid-argument", "validation");
                }

                return SuccessResponse(request, PanelVisibilityContract.Method, cached.Response);
            }

            _panelVisible = report.PanelVisible;
            _visibilityRevision++;
            _visibilityReportCount++;

            var response = new PanelVisibilityReportResponse
            {
                ClientOperationId = report.ClientOperationId,
                PanelVisible = report.PanelVisible,
                Revision = _visibilityRevision,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
            };

            CacheOperation(report, response);
            return SuccessResponse(request, PanelVisibilityContract.Method, response);
        }
    }

    private static bool TryDeserializePayload<T>(JsonElement payload, out T? value)
        where T : class
    {
        value = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            value = payload.Deserialize<T>(ContractJson.Options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidOperationId(Guid value) => value != Guid.Empty;

    private static bool IsValidRequiredText(string? value, int maxLength) =>
        IsValidText(value, maxLength) && !string.IsNullOrWhiteSpace(value);

    private static bool IsValidText(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength;

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

    private static bool TryParseBodyFormat(string? value, out NoteBodyFormat bodyFormat)
    {
        if (string.Equals(value, NotesContract.PlainTextFormat, StringComparison.Ordinal))
        {
            bodyFormat = NoteBodyFormat.PlainText;
            return true;
        }

        if (string.Equals(value, NotesContract.MarkdownFormat, StringComparison.Ordinal))
        {
            bodyFormat = NoteBodyFormat.Markdown;
            return true;
        }

        bodyFormat = default;
        return false;
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

    private static WeatherSettingsDto ToContract(WeatherSettingsRecord settings) =>
        new()
        {
            InstanceId = settings.InstanceId,
            Label = settings.Label,
            Latitude = settings.Latitude,
            Longitude = settings.Longitude,
            Revision = settings.Revision,
            UpdatedAtUtc = settings.UpdatedAtUtc ?? string.Empty,
        };

    private static LayoutDto ToContract(LayoutRecord layout) =>
        new()
        {
            LayoutId = layout.LayoutId,
            DisplayId = layout.DisplayId,
            Revision = layout.Revision,
            Items = layout.Items
                .OrderBy(item => item.OrderIndex)
                .Select(item => new LayoutItemDto
                {
                    InstanceId = item.InstanceId,
                    Order = item.OrderIndex,
                    ColumnSpan = item.ColumnSpan,
                    RowSpan = item.RowSpan,
                    SizeId = item.SizeId,
                    PreferredColumn = item.PreferredColumn,
                    PreferredRow = item.PreferredRow,
                })
                .ToArray(),
            UpdatedAtUtc = layout.UpdatedAtUtc,
        };

    private static bool IsValidLayoutIdentity(string? layoutId, string? displayId) =>
        IsValidRequiredText(layoutId, LayoutContract.MaxLayoutIdLength) &&
        IsValidRequiredText(displayId, LayoutContract.MaxDisplayIdLength);

    private static bool IsValidWeatherInstanceId(string? instanceId) =>
        WeatherSettingsContract.IsValidInstanceId(instanceId) &&
        string.Equals(
            instanceId,
            OpenMeteoWeatherProvider.InstanceId,
            StringComparison.Ordinal);

    private static bool TryMapLayoutItems(
        IReadOnlyList<LayoutItemDto>? payloadItems,
        out IReadOnlyList<LayoutItemRecord>? items)
    {
        items = null;
        if (payloadItems is null || payloadItems.Count > LayoutContract.MaxItems)
        {
            return false;
        }

        var mapped = new List<LayoutItemRecord>(payloadItems.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (LayoutItemDto item in payloadItems)
        {
            if (item is null ||
                !IsValidRequiredText(item.InstanceId, LayoutContract.MaxInstanceIdLength) ||
                item.Order < 0 ||
                !ids.Add(item.InstanceId) ||
                !orders.Add(item.Order) ||
                item.PreferredColumn is < 0 ||
                item.PreferredRow is < 0 ||
                !LayoutContract.TryGetDeclaredSpan(
                    item.SizeId,
                    out int declaredColumns,
                    out int declaredRows) ||
                item.ColumnSpan != declaredColumns ||
                item.RowSpan != declaredRows)
            {
                return false;
            }

            mapped.Add(new LayoutItemRecord(
                item.InstanceId,
                item.Order,
                item.ColumnSpan,
                item.RowSpan,
                item.SizeId,
                item.PreferredColumn,
                item.PreferredRow));
        }

        if (!orders.SetEquals(Enumerable.Range(0, payloadItems.Count)))
        {
            return false;
        }

        items = mapped.AsReadOnly();
        return true;
    }

    private bool TryGetCachedNoteOperation(
        Guid operationId,
        object fingerprint,
        out object? response,
        out bool fingerprintConflict)
    {
        if (!_cachedNoteOperations.TryGetValue(operationId, out CachedNoteOperation? cached))
        {
            response = null;
            fingerprintConflict = false;
            return false;
        }

        response = cached.Response;
        fingerprintConflict = !Equals(cached.Fingerprint, fingerprint);
        return true;
    }

    private void CacheNoteOperation(Guid operationId, object fingerprint, object response)
    {
        while (_cachedNoteOperations.Count >= MaxCachedOperations && _noteOperationOrder.Count > 0)
        {
            _cachedNoteOperations.Remove(_noteOperationOrder.Dequeue());
        }

        _cachedNoteOperations[operationId] = new CachedNoteOperation(fingerprint, response);
        _noteOperationOrder.Enqueue(operationId);
    }

    private bool TryGetCachedLayoutOperation(
        Guid operationId,
        string fingerprint,
        out LayoutSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedLayoutOperations.TryGetValue(operationId, out CachedLayoutOperation? cached))
        {
            response = null;
            fingerprintConflict = false;
            return false;
        }

        response = cached.Response;
        fingerprintConflict = !string.Equals(
            cached.Fingerprint,
            fingerprint,
            StringComparison.Ordinal);
        return true;
    }

    private void CacheLayoutOperation(
        Guid operationId,
        string fingerprint,
        LayoutSaveResponse response)
    {
        while (_cachedLayoutOperations.Count >= MaxCachedOperations &&
            _layoutOperationOrder.Count > 0)
        {
            _cachedLayoutOperations.Remove(_layoutOperationOrder.Dequeue());
        }

        _cachedLayoutOperations[operationId] = new CachedLayoutOperation(
            fingerprint,
            response);
        _layoutOperationOrder.Enqueue(operationId);
    }

    private bool TryGetCachedWeatherSettingsOperation(
        Guid operationId,
        WeatherSettingsSaveFingerprint fingerprint,
        out WeatherSettingsSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedWeatherSettingsOperations.TryGetValue(
                operationId,
                out CachedWeatherSettingsOperation? cached))
        {
            response = null;
            fingerprintConflict = false;
            return false;
        }

        response = cached.Response;
        fingerprintConflict = !Equals(cached.Fingerprint, fingerprint);
        return true;
    }

    private void CacheWeatherSettingsOperation(
        Guid operationId,
        WeatherSettingsSaveFingerprint fingerprint,
        WeatherSettingsSaveResponse response)
    {
        while (_cachedWeatherSettingsOperations.Count >= MaxCachedOperations &&
            _weatherSettingsOperationOrder.Count > 0)
        {
            _cachedWeatherSettingsOperations.Remove(
                _weatherSettingsOperationOrder.Dequeue());
        }

        _cachedWeatherSettingsOperations[operationId] =
            new CachedWeatherSettingsOperation(fingerprint, response);
        _weatherSettingsOperationOrder.Enqueue(operationId);
    }

    private void CacheOperation(
        PanelVisibilityReportRequest request,
        PanelVisibilityReportResponse response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations && _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations.Add(
            request.ClientOperationId,
            new CachedOperation(request, response));
        _operationOrder.Enqueue(request.ClientOperationId);
    }

    private static bool Matches(
        PanelVisibilityReportRequest left,
        PanelVisibilityReportRequest right) =>
        left.PanelVisible == right.PanelVisible &&
        string.Equals(left.DisplayId, right.DisplayId, StringComparison.Ordinal);

    private static Envelope SuccessResponse(
        Envelope request,
        string method,
        object payload) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Response,
            MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = method,
            Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options),
            Error = null,
        };

    private static Envelope ErrorResponse(
        Envelope request,
        string code,
        string category) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Response,
            MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = request.Method,
            Payload = JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
            Error = new ContractError
            {
                Code = code,
                Category = category,
                MessageKey = $"error.{code.Replace('.', '-')}",
                DeveloperMessage = null,
                CorrelationId = request.MessageId,
                IsTransient = false,
                RetryAfterSeconds = null,
                Details = null,
            },
        };

    private sealed record CachedOperation(
        PanelVisibilityReportRequest Request,
        PanelVisibilityReportResponse Response);

    private sealed record CachedNoteOperation(object Fingerprint, object Response);

    private sealed record CachedLayoutOperation(
        string Fingerprint,
        LayoutSaveResponse Response);

    private sealed record CachedWeatherSettingsOperation(
        WeatherSettingsSaveFingerprint Fingerprint,
        WeatherSettingsSaveResponse Response);

    private sealed record NoteSaveFingerprint(
        string NoteId,
        string Title,
        string Body,
        string BodyFormat,
        string? ExpectedUpdatedAtUtc);

    private sealed record NoteDeleteFingerprint(
        string NoteId,
        string ExpectedUpdatedAtUtc);

    private sealed record WeatherSettingsSaveFingerprint(
        string InstanceId,
        string Label,
        double Latitude,
        double Longitude,
        int ExpectedRevision);
}
