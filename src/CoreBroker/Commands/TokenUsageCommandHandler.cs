using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class TokenUsageCommandHandler
{
    private readonly object _gate;
    private readonly TokenUsageRuntime _runtime;
    private readonly Dictionary<Guid, CachedTokenUsageOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();

    public TokenUsageCommandHandler(TokenUsageRuntime runtime, object gate)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public Envelope Handle(Envelope request)
    {
        if (string.Equals(
                request.Method,
                TokenUsageContract.SettingsGetMethod,
                StringComparison.Ordinal))
        {
            return HandleGet(request);
        }

        if (string.Equals(
                request.Method,
                TokenUsageContract.SettingsSaveMethod,
                StringComparison.Ordinal))
        {
            return HandleSave(request);
        }

        return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
    }

    /// <summary>
    /// The one token-usage command that does not take the router gate: it is an outbound
    /// network call, and holding the gate across it would stall every other domain behind
    /// a remote host. Failure is reported as provider.failed - the prices already on hand
    /// stay in force, so it is transient by nature.
    /// </summary>
    public async Task<Envelope> SyncPricingAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out TokenUsagePricingSyncRequest? payload) ||
            payload is null ||
            !IsValidUsageInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        Task<TokenUsagePricingAttemptResult>? attempt;
        try
        {
            attempt = _runtime.SyncPricingNowAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (attempt is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        TokenUsagePricingAttemptResult result = await attempt.ConfigureAwait(false);
        if (result == TokenUsagePricingAttemptResult.Failed)
        {
            return ErrorResponse(request, "provider.failed", "provider");
        }

        return SuccessResponse(
            request,
            TokenUsageContract.PricingSyncMethod,
            new TokenUsagePricingSyncResponse
            {
                Updated = result == TokenUsagePricingAttemptResult.Updated,
                RateCount = _runtime.RateBook.SyncedCount,
                PricingSyncedAtUtc = _runtime.RateBook.SyncedAtUtc is { } syncedAt
                    ? NoteRecord.FormatTimestamp(syncedAt)
                    : string.Empty,
            });
    }

    private Envelope HandleGet(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out TokenUsageSettingsGetRequest? payload) ||
            payload is null ||
            !IsValidUsageInstanceId(payload.InstanceId))
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        // Probed before taking the gate: it touches the file system, not any shared state, and
        // holding the router's single lock across directory checks would put every other
        // command behind it.
        IReadOnlyList<TokenUsageVendorStatusDto> vendors = _runtime.ListVendors();

        lock (_gate)
        {
            try
            {
                return SuccessResponse(
                    request,
                    TokenUsageContract.SettingsGetMethod,
                    new TokenUsageSettingsGetResponse
                    {
                        Settings = ToContract(_runtime.GetSettings()),
                        Vendors = vendors,
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

    private Envelope HandleSave(Envelope request)
    {
        if (!TryDeserializePayload(
                request.Payload,
                out TokenUsageSettingsSaveRequest? payload) ||
            payload is null ||
            !IsValidOperationId(payload.ClientOperationId) ||
            !IsValidUsageInstanceId(payload.InstanceId) ||
            !TokenUsageContract.IsValidVendorList(payload.EnabledVendors) ||
            payload.ExpectedRevision < 0)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        var fingerprint = new TokenUsageSaveFingerprint(
            payload.InstanceId!,
            string.Join('|', payload.EnabledVendors!),
            payload.SyncPricing,
            payload.ExpectedRevision);

        lock (_gate)
        {
            if (TryGetCachedOperation(
                    payload.ClientOperationId,
                    fingerprint,
                    out TokenUsageSettingsSaveResponse? cachedResponse,
                    out bool fingerprintConflict))
            {
                return fingerprintConflict
                    ? ErrorResponse(request, "validation.invalid-argument", "validation")
                    : SuccessResponse(
                        request,
                        TokenUsageContract.SettingsSaveMethod,
                        cachedResponse!);
            }

            try
            {
                TokenUsageSettingsRecord saved = _runtime.SaveSettings(
                    payload.InstanceId!,
                    payload.EnabledVendors!,
                    payload.ExpectedRevision,
                    payload.SyncPricing);
                var responsePayload = new TokenUsageSettingsSaveResponse
                {
                    ClientOperationId = payload.ClientOperationId,
                    Settings = ToContract(saved),
                };
                CacheOperation(payload.ClientOperationId, fingerprint, responsePayload);
                return SuccessResponse(
                    request,
                    TokenUsageContract.SettingsSaveMethod,
                    responsePayload);
            }
            catch (TokenUsageSettingsRevisionConflictException)
            {
                return ErrorResponse(
                    request,
                    "conflict.tokenusage-settings-revision",
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

    private TokenUsageSettingsDto ToContract(TokenUsageSettingsRecord settings) =>
        new()
        {
            InstanceId = settings.InstanceId,
            EnabledVendors = settings.EnabledVendors,
            SyncPricing = settings.SyncPricing,
            PricingSyncedAtUtc = _runtime.RateBook.SyncedAtUtc is { } syncedAt
                ? NoteRecord.FormatTimestamp(syncedAt)
                : string.Empty,
            Revision = settings.Revision,
            UpdatedAtUtc = settings.UpdatedAtUtc ?? string.Empty,
        };

    private static bool IsValidUsageInstanceId(string? instanceId) =>
        TokenUsageContract.IsValidInstanceId(instanceId) &&
        string.Equals(instanceId, TokenUsageProvider.InstanceId, StringComparison.Ordinal);

    private bool TryGetCachedOperation(
        Guid operationId,
        TokenUsageSaveFingerprint fingerprint,
        out TokenUsageSettingsSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedOperations.TryGetValue(
                operationId,
                out CachedTokenUsageOperation? cached))
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
        TokenUsageSaveFingerprint fingerprint,
        TokenUsageSettingsSaveResponse response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations && _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations[operationId] = new CachedTokenUsageOperation(fingerprint, response);
        _operationOrder.Enqueue(operationId);
    }

    private sealed record CachedTokenUsageOperation(
        TokenUsageSaveFingerprint Fingerprint,
        TokenUsageSettingsSaveResponse Response);

    private sealed record TokenUsageSaveFingerprint(
        string InstanceId,
        string EnabledVendors,
        bool? SyncPricing,
        int ExpectedRevision);
}
