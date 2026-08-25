using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class SystemMonitorCommandHandler
{
    private readonly object _gate;
    private readonly SystemMonitorRuntime _runtime;
    private readonly Dictionary<Guid, CachedSystemMonitorOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();

    public SystemMonitorCommandHandler(SystemMonitorRuntime runtime, object gate)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public Envelope Handle(Envelope request)
    {
        if (string.Equals(
                request.Method,
                SystemMonitorContract.SettingsGetMethod,
                StringComparison.Ordinal))
        {
            return HandleGet(request);
        }

        if (string.Equals(
                request.Method,
                SystemMonitorContract.SettingsSaveMethod,
                StringComparison.Ordinal))
        {
            return HandleSave(request);
        }

        if (string.Equals(
                request.Method,
                SystemMonitorContract.SummaryGetMethod,
                StringComparison.Ordinal))
        {
            return HandleSummaryGet(request);
        }

        return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
    }

    private Envelope HandleGet(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out SystemMonitorSettingsGetRequest? payload) ||
            payload is null ||
            !IsValidMonitorInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        // Enumerated before taking the gate: it reads the machine, not any shared state, and
        // holding the router's single lock across an OS enumeration would put every other
        // command behind it.
        IReadOnlyList<SystemMonitorNetworkInterfaceDto> interfaces =
            SystemMonitorRuntime.ListNetworkInterfaces();

        lock (_gate)
        {
            try
            {
                return SuccessResponse(
                    request,
                    SystemMonitorContract.SettingsGetMethod,
                    new SystemMonitorSettingsGetResponse
                    {
                        Settings = ToContract(_runtime.GetSettings()),
                        NetworkInterfaces = interfaces,
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

    /// <summary>
    /// Read-only and lock-free on the runtime side, so the taskbar entry's two-second poll
    /// never queues behind a note autosave or a layout write.
    /// </summary>
    private Envelope HandleSummaryGet(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out SystemMonitorSummaryGetRequest? payload) ||
            payload is null ||
            !IsValidMonitorInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        try
        {
            return SuccessResponse(
                request,
                SystemMonitorContract.SummaryGetMethod,
                new SystemMonitorSummaryGetResponse
                {
                    Summary = _runtime.TryGetSummary(),
                });
        }
        catch (ObjectDisposedException)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }
    }

    private Envelope HandleSave(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out SystemMonitorSettingsSaveRequest? payload) ||
            payload is null ||
            !IsValidOperationId(payload.ClientOperationId) ||
            !IsValidMonitorInstanceId(payload.InstanceId) ||
            !SystemMonitorContract.IsValidItemList(payload.CardItems) ||
            !SystemMonitorContract.IsValidItemList(payload.EntryItems) ||
            !SystemMonitorContract.IsValidNetworkInterfaceId(payload.NetworkInterfaceId) ||
            payload.ExpectedRevision < 0)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        var fingerprint = new SystemMonitorSaveFingerprint(
            payload.InstanceId!,
            Describe(payload.CardItems!),
            Describe(payload.EntryItems!),
            SystemMonitorContract.NormalizeNetworkInterfaceId(payload.NetworkInterfaceId),
            payload.ExpectedRevision);

        lock (_gate)
        {
            if (TryGetCachedOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out SystemMonitorSettingsSaveResponse? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(
                        request,
                        SystemMonitorContract.SettingsSaveMethod,
                        cachedResponse!);
            }

            try
            {
                SystemMonitorSettingsRecord saved = _runtime.SaveSettings(
                    payload.InstanceId!,
                    payload.CardItems!,
                    payload.EntryItems!,
                    payload.ExpectedRevision,
                    payload.NetworkInterfaceId);
                var responsePayload = new SystemMonitorSettingsSaveResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Settings = ToContract(saved),
                };
                CacheOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return SuccessResponse(
                    request,
                    SystemMonitorContract.SettingsSaveMethod,
                    responsePayload);
            }
            catch (SystemMonitorSettingsRevisionConflictException)
            {
                return ErrorResponse(
                    request,
                    "conflict.sysmon-settings-revision",
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
        }
    }

    private static SystemMonitorSettingsDto ToContract(SystemMonitorSettingsRecord settings) =>
        new()
        {
            InstanceId = settings.InstanceId,
            CardItems = settings.CardItems,
            EntryItems = settings.EntryItems,
            NetworkInterfaceId = settings.NetworkInterfaceId,
            Revision = settings.Revision,
            UpdatedAtUtc = settings.UpdatedAtUtc ?? string.Empty,
        };

    /// <summary>
    /// A stable text form of one item list, so a replayed operation ID carrying a different
    /// configuration is detected as the mistake it is.
    /// </summary>
    private static string Describe(IReadOnlyList<SystemMonitorItemDto> items) =>
        string.Join('|', items.Select(item => $"{item.MetricId}:{item.Detail}"));

    private static bool IsValidMonitorInstanceId(string? instanceId) =>
        SystemMonitorContract.IsValidInstanceId(instanceId) &&
        string.Equals(
            instanceId,
            SystemMonitorProvider.InstanceId,
            StringComparison.Ordinal);

    private bool TryGetCachedOperation(
        Guid operationId,
        SystemMonitorSaveFingerprint fingerprint,
        out SystemMonitorSettingsSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedOperations.TryGetValue(
                operationId,
                out CachedSystemMonitorOperation? cached))
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
        SystemMonitorSaveFingerprint fingerprint,
        SystemMonitorSettingsSaveResponse response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations &&
            _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations[operationId] =
            new CachedSystemMonitorOperation(fingerprint, response);
        _operationOrder.Enqueue(operationId);
    }

    private sealed record CachedSystemMonitorOperation(
        SystemMonitorSaveFingerprint Fingerprint,
        SystemMonitorSettingsSaveResponse Response);

    private sealed record SystemMonitorSaveFingerprint(
        string InstanceId,
        string CardItems,
        string EntryItems,
        string NetworkInterfaceId,
        int ExpectedRevision);
}
