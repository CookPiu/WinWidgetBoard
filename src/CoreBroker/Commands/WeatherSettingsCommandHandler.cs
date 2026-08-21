using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class WeatherSettingsCommandHandler
{
    private readonly object _gate;
    private readonly WeatherProviderRuntime _weatherProviderRuntime;
    private readonly OpenMeteoGeocodingService? _geocodingService;
    private readonly Dictionary<Guid, CachedWeatherSettingsOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();

    public WeatherSettingsCommandHandler(
        WeatherProviderRuntime weatherProviderRuntime,
        object gate,
        OpenMeteoGeocodingService? geocodingService = null)
    {
        _weatherProviderRuntime = weatherProviderRuntime ??
            throw new ArgumentNullException(nameof(weatherProviderRuntime));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _geocodingService = geocodingService;
    }

    public bool LocationSearchAvailable => _geocodingService is not null;

    /// <summary>
    /// Location search is the one weather command that does not take the router gate: it is
    /// an outbound network call, and holding the gate across it would stall every other
    /// domain behind a remote host. It reads and writes no shared state, so it does not
    /// need the gate to be correct.
    /// </summary>
    public async Task<Envelope> SearchLocationsAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        if (_geocodingService is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (!TryDeserializePayload(
                request.Payload,
                out WeatherLocationSearchRequest? payload) ||
            payload is null ||
            !WeatherLocationSearchContract.TryNormalizeQuery(
                payload.Query,
                out string? query) ||
            query is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        try
        {
            IReadOnlyList<WeatherLocationCandidateDto> results = await _geocodingService
                .SearchAsync(query, cancellationToken)
                .ConfigureAwait(false);
            return SuccessResponse(
                request,
                WeatherLocationSearchContract.SearchMethod,
                new WeatherLocationSearchResponse { Results = results });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                IOException or
                InvalidDataException or
                JsonException or
                OperationCanceledException)
        {
            // The dialog degrades to "search unavailable" and the user can still keep the
            // location they already have; nothing here is worth failing the connection over.
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }
    }

    public Envelope Handle(Envelope request)
    {
        if (string.Equals(
                request.Method,
                WeatherSettingsContract.GetMethod,
                StringComparison.Ordinal))
        {
            return HandleGet(request);
        }

        if (string.Equals(
                request.Method,
                WeatherSettingsContract.SaveMethod,
                StringComparison.Ordinal))
        {
            return HandleSave(request);
        }

        if (string.Equals(
                request.Method,
                WeatherSettingsContract.SummaryGetMethod,
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

    // Read-only and lock-free on the runtime side, so it does not queue behind a save.
    private Envelope HandleSummaryGet(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out WeatherSummaryGetRequest? payload) ||
            payload is null ||
            !IsValidWeatherInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        try
        {
            return SuccessResponse(
                request,
                WeatherSettingsContract.SummaryGetMethod,
                new WeatherSummaryGetResponse
                {
                    Summary = _weatherProviderRuntime.TryGetSummary(),
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
            if (TryGetCachedOperation(
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
                CacheOperation(
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

    private static bool IsValidWeatherInstanceId(string? instanceId) =>
        WeatherSettingsContract.IsValidInstanceId(instanceId) &&
        string.Equals(
            instanceId,
            OpenMeteoWeatherProvider.InstanceId,
            StringComparison.Ordinal);

    private bool TryGetCachedOperation(
        Guid operationId,
        WeatherSettingsSaveFingerprint fingerprint,
        out WeatherSettingsSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedOperations.TryGetValue(
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

    private void CacheOperation(
        Guid operationId,
        WeatherSettingsSaveFingerprint fingerprint,
        WeatherSettingsSaveResponse response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations &&
            _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations[operationId] =
            new CachedWeatherSettingsOperation(fingerprint, response);
        _operationOrder.Enqueue(operationId);
    }

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
