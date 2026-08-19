using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class PanelVisibilityCommandHandler
{
    private readonly object _gate;
    private readonly Dictionary<Guid, CachedOperation> _cachedOperations = new();
    private readonly Queue<Guid> _operationOrder = new();
    private bool _panelVisible;
    private long _visibilityRevision;
    private int _visibilityReportCount;

    public PanelVisibilityCommandHandler(object gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

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

    public Envelope Handle(Envelope request)
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
}
