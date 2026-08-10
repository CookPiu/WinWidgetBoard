namespace WinWidgetBoard.WorkspacePanel.Layout;

public readonly record struct GridCell(int Column, int Row);

public static class CardDragPlacementProjector
{
    private const int MaxProjectedRow = 10_000;

    public static bool TryProject(
        int columnCount,
        IReadOnlyList<CardLayoutItem> items,
        IReadOnlyList<CardPlacement> currentPlacements,
        string draggedInstanceId,
        GridCell requestedCell,
        out IReadOnlyList<CardPlacement> placements)
    {
        _ = ResponsiveGridLayout.Pack(
            columnCount,
            Array.Empty<CardLayoutItem>());
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(currentPlacements);
        ArgumentException.ThrowIfNullOrWhiteSpace(draggedInstanceId);

        Dictionary<string, CardLayoutItem> itemsById = new(
            StringComparer.Ordinal);
        foreach (CardLayoutItem item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.InstanceId);
            if (!itemsById.TryAdd(item.InstanceId, item))
            {
                throw new ArgumentException(
                    $"Duplicate card instance ID '{item.InstanceId}'.",
                    nameof(items));
            }
        }

        if (!itemsById.TryGetValue(
                draggedInstanceId,
                out CardLayoutItem? draggedItem) ||
            draggedItem is null)
        {
            placements = Array.Empty<CardPlacement>();
            return false;
        }

        Dictionary<string, CardPlacement> placementById = new(
            StringComparer.Ordinal);
        foreach (CardPlacement placement in currentPlacements)
        {
            if (!placementById.TryAdd(placement.InstanceId, placement))
            {
                throw new ArgumentException(
                    $"Duplicate placement instance ID '{placement.InstanceId}'.",
                    nameof(currentPlacements));
            }
        }

        if (placementById.Count != itemsById.Count ||
            itemsById.Keys.Any(instanceId => !placementById.ContainsKey(instanceId)))
        {
            throw new ArgumentException(
                "Current placements must contain exactly one placement for each card.",
                nameof(currentPlacements));
        }

        CardSpan span = ResponsiveGridLayout.GetSpan(draggedItem.Size);
        int columnSpan = Math.Min(span.Columns, columnCount);
        int maxColumn = columnCount - columnSpan;
        int requestedColumn = Math.Clamp(requestedCell.Column, 0, maxColumn);
        int bottomExclusive = placementById.Count == 0
            ? 0
            : placementById.Values.Max(placement => placement.BottomExclusive);
        int maxRequestedRow = Math.Min(bottomExclusive, MaxProjectedRow);
        int requestedRow = Math.Clamp(requestedCell.Row, 0, maxRequestedRow);
        CardPlacement draggedStart = placementById[draggedInstanceId];
        var draggedTarget = new CardPlacement(
            draggedInstanceId,
            draggedItem.Size,
            requestedColumn,
            requestedRow,
            columnSpan,
            span.Rows);

        var itemOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < items.Count; index++)
        {
            itemOrder.Add(items[index].InstanceId, index);
        }
        var projectionInput = new List<CardLayoutItem>(items.Count)
        {
            draggedItem with
            {
                PreferredColumn = requestedColumn,
                PreferredRow = requestedRow,
            },
        };
        projectionInput.AddRange(
            currentPlacements
                .Where(placement => !string.Equals(
                    placement.InstanceId,
                    draggedInstanceId,
                    StringComparison.Ordinal))
                .OrderBy(placement => placement.Row)
                .ThenBy(placement => placement.Column)
                .ThenBy(placement => itemOrder[placement.InstanceId])
                .Select(placement => itemsById[placement.InstanceId] with
                {
                    PreferredColumn = Overlaps(placement, draggedTarget)
                        ? draggedStart.Column
                        : placement.Column,
                    PreferredRow = Overlaps(placement, draggedTarget)
                        ? draggedStart.Row
                        : placement.Row,
                }));

        IReadOnlyList<CardPlacement> packed = ResponsiveGridLayout.Pack(
            columnCount,
            projectionInput);
        Dictionary<string, CardPlacement> projectedById = packed.ToDictionary(
            placement => placement.InstanceId,
            StringComparer.Ordinal);

        List<CardPlacement> projected = new(items.Count);
        foreach (CardLayoutItem item in items)
        {
            projected.Add(projectedById[item.InstanceId]);
        }

        placements = projected.AsReadOnly();
        return true;
    }

    private static bool Overlaps(
        CardPlacement left,
        CardPlacement right)
    {
        return left.Column < right.RightExclusive &&
            right.Column < left.RightExclusive &&
            left.Row < right.BottomExclusive &&
            right.Row < left.BottomExclusive;
    }
}
