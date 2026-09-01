using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// Coordinates layout persistence without depending on WinUI controls.
/// It owns protocol mapping, revision protection, and persisted-layout replay validation.
/// </summary>
public sealed class LayoutPersistenceCoordinator
{
    private const string PersistedLayoutId = "primary-default";
    private const string PersistedDisplayId = "primary";

    private readonly ILayoutClient _layoutClient;
    private readonly CardLayoutViewModel _layout;
    private int _revision;

    public LayoutPersistenceCoordinator(
        ILayoutClient layoutClient,
        CardLayoutViewModel layout)
    {
        _layoutClient = layoutClient ?? throw new ArgumentNullException(nameof(layoutClient));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public async Task<LayoutLoadResult?> LoadAsync(
        CancellationToken cancellationToken)
    {
        LayoutDto? persisted = await _layoutClient
            .GetLayoutAsync(
                PersistedLayoutId,
                PersistedDisplayId,
                cancellationToken)
            .ConfigureAwait(false);

        if (persisted is null)
        {
            return null;
        }

        var loadedItems = new List<CardLayoutItem>(persisted.Items.Count);
        var loadedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LayoutItemDto item in persisted.Items.OrderBy(item => item.Order))
        {
            if (!TryParseCardSize(item.SizeId, out CardSize size) ||
                !loadedIds.Add(item.InstanceId))
            {
                throw new InvalidDataException(
                    "The persisted layout contains an invalid card item.");
            }

            // Layouts written before the placeholders were retired still carry them. They
            // are dropped on replay - not an error, just a card the board no longer shows -
            // and the next layout save persists their absence.
            if (BuiltInCardCatalog.DeferredInstanceIds.Contains(
                    BuiltInCardCatalog.BaseInstanceId(item.InstanceId),
                    StringComparer.Ordinal))
            {
                continue;
            }

            loadedItems.Add(new CardLayoutItem(
                item.InstanceId,
                size,
                item.PreferredColumn,
                item.PreferredRow));
        }

        CardLayoutReplayResult replay = CardLayoutReplaySanitizer.Normalize(
            _layout.ColumnCount,
            loadedItems);
        Volatile.Write(ref _revision, persisted.Revision);
        return new LayoutLoadResult(
            replay.Items,
            replay.RecoveredItemCount);
    }

    public LayoutSaveRequest CreateSaveRequest()
    {
        LayoutItemDto[] items = _layout.GetItemsInPlacementOrder()
            .Select((item, index) =>
            {
                CardSpan span = ResponsiveGridLayout.GetSpan(item.Size);
                if (!_layout.TryGetPlacement(
                        item.InstanceId,
                        out CardPlacement placement))
                {
                    throw new InvalidOperationException(
                        $"The card '{item.InstanceId}' has no current placement.");
                }

                return new LayoutItemDto
                {
                    InstanceId = item.InstanceId,
                    Order = index,
                    ColumnSpan = span.Columns,
                    RowSpan = span.Rows,
                    SizeId = ToLayoutSizeId(item.Size),
                    PreferredColumn = placement.Column,
                    PreferredRow = placement.Row,
                };
            })
            .ToArray();

        return new LayoutSaveRequest
        {
            ClientOperationId = Guid.NewGuid(),
            LayoutId = PersistedLayoutId,
            DisplayId = PersistedDisplayId,
            ExpectedRevision = Volatile.Read(ref _revision),
            Items = items,
        };
    }

    public async Task<LayoutDto> SaveAsync(
        LayoutSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LayoutDto saved = await _layoutClient
            .SaveLayoutAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _revision, saved.Revision);
        return saved;
    }

    private static string ToLayoutSizeId(CardSize size) => size switch
    {
        CardSize.S => "s",
        CardSize.M => "m",
        CardSize.L => "l",
        CardSize.W => "w",
        CardSize.XL => "xl",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    private static bool TryParseCardSize(
        string? sizeId,
        out CardSize size)
    {
        size = sizeId switch
        {
            "s" => CardSize.S,
            "m" => CardSize.M,
            "l" => CardSize.L,
            "w" => CardSize.W,
            "xl" => CardSize.XL,
            _ => default,
        };
        return sizeId is "s" or "m" or "l" or "w" or "xl";
    }
}

public sealed record LayoutLoadResult(
    IReadOnlyList<CardLayoutItem> Items,
    int RecoveredItemCount);
