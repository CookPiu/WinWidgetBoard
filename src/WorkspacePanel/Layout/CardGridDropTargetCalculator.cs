namespace WinWidgetBoard.WorkspacePanel.Layout;

public static class CardGridDropTargetCalculator
{
    private const double FallbackCellWidth = 180;

    public static GridCell GetDropCell(
        int columnCount,
        double availableWidth,
        double columnGap,
        double rowHeight,
        double rowGap,
        CardPlacement placement,
        double offsetX,
        double offsetY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);

        if (!double.IsFinite(columnGap) || columnGap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columnGap));
        }

        if (!double.IsFinite(rowHeight) || rowHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowHeight));
        }

        if (!double.IsFinite(rowGap) || rowGap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowGap));
        }

        double width = double.IsFinite(availableWidth) && availableWidth > 0
            ? availableWidth
            : columnCount * FallbackCellWidth +
                (columnCount - 1) * columnGap;
        double cellWidth = Math.Max(
            0,
            (width - (columnCount - 1) * columnGap) / columnCount);
        double columnStride = cellWidth + columnGap;
        double rowStride = rowHeight + rowGap;

        int requestedColumn = columnStride <= 0
            ? placement.Column
            : (int)Math.Round(
                (placement.Column * columnStride + offsetX) / columnStride,
                MidpointRounding.AwayFromZero);
        int requestedRow = rowStride <= 0
            ? placement.Row
            : (int)Math.Round(
                (placement.Row * rowStride + offsetY) / rowStride,
                MidpointRounding.AwayFromZero);

        int columnSpan = Math.Clamp(placement.ColumnSpan, 1, columnCount);
        int maxColumn = columnCount - columnSpan;
        return new GridCell(
            Math.Clamp(requestedColumn, 0, maxColumn),
            Math.Max(0, requestedRow));
    }
}
