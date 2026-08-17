using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

public sealed class CoreBrokerCommandRouter
{
    private const string SessionPingMethod = "session.ping";
    private const int MaxCachedOperations = 512;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CachedOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();
    private readonly NoteCommandHandler? _noteCommandHandler;
    private readonly LayoutRepository? _layoutRepository;
    private readonly CardSnapshotSubscriptionHub? _cardSnapshotSubscriptionHub;
    private readonly ProviderRefreshVisibilityRegistry? _providerVisibilityRegistry;
    private readonly WeatherProviderRuntime? _weatherProviderRuntime;
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
        _noteCommandHandler = noteRepository is null
            ? null
            : new NoteCommandHandler(noteRepository, _gate);
        _layoutRepository = layoutRepository;
        _cardSnapshotSubscriptionHub = cardSnapshotSubscriptionHub;
        _providerVisibilityRegistry = providerVisibilityRegistry;
        _weatherProviderRuntime = weatherProviderRuntime;
    }

    public bool NotesAvailable => _noteCommandHandler is not null;

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

        if (NotesContract.Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return _noteCommandHandler?.Handle(request) ??
                ErrorResponse(request, "resource.unavailable", "resource-unavailable");
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

    private sealed record CachedOperation(
        PanelVisibilityReportRequest Request,
        PanelVisibilityReportResponse Response);

    private sealed record CachedLayoutOperation(
        string Fingerprint,
        LayoutSaveResponse Response);

    private sealed record CachedWeatherSettingsOperation(
        WeatherSettingsSaveFingerprint Fingerprint,
        WeatherSettingsSaveResponse Response);

    private sealed record WeatherSettingsSaveFingerprint(
        string InstanceId,
        string Label,
        double Latitude,
        double Longitude,
        int ExpectedRevision);
}
