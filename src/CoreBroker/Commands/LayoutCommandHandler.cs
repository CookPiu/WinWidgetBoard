using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class LayoutCommandHandler
{
    private readonly object _gate;
    private readonly LayoutRepository _layoutRepository;
    private readonly Dictionary<Guid, CachedLayoutOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();

    public LayoutCommandHandler(LayoutRepository layoutRepository, object gate)
    {
        _layoutRepository = layoutRepository ??
            throw new ArgumentNullException(nameof(layoutRepository));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public Envelope Handle(Envelope request)
    {
        if (string.Equals(request.Method, LayoutContract.GetMethod, StringComparison.Ordinal))
        {
            return HandleGet(request);
        }

        if (string.Equals(request.Method, LayoutContract.SaveMethod, StringComparison.Ordinal))
        {
            return HandleSave(request);
        }

        return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
    }

    private Envelope HandleGet(Envelope request)
    {
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

    private Envelope HandleSave(Envelope request)
    {
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
            if (TryGetCachedOperation(
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
                CacheOperation(
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

    private bool TryGetCachedOperation(
        Guid operationId,
        string fingerprint,
        out LayoutSaveResponse? response,
        out bool fingerprintConflict)
    {
        if (!_cachedOperations.TryGetValue(operationId, out CachedLayoutOperation? cached))
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

    private void CacheOperation(
        Guid operationId,
        string fingerprint,
        LayoutSaveResponse response)
    {
        while (_cachedOperations.Count >= MaxCachedOperations &&
            _operationOrder.Count > 0)
        {
            _cachedOperations.Remove(_operationOrder.Dequeue());
        }

        _cachedOperations[operationId] = new CachedLayoutOperation(
            fingerprint,
            response);
        _operationOrder.Enqueue(operationId);
    }

    private sealed record CachedLayoutOperation(
        string Fingerprint,
        LayoutSaveResponse Response);
}
