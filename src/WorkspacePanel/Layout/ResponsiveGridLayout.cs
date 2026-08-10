namespace WinWidgetBoard.WorkspacePanel.Layout;

public enum CardSize
{
    S,
    M,
    L,
    W,
    XL,
}

public readonly record struct CardSpan(int Columns, int Rows);

public sealed record CardLayoutItem(
    string InstanceId,
    CardSize Size,
    int? PreferredColumn = null,
    int? PreferredRow = null);

public readonly record struct CardPlacement(
    string InstanceId,
    CardSize Size,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan)
{
    public int RightExclusive => Column + ColumnSpan;

    public int BottomExclusive => Row + RowSpan;
}

public static class ResponsiveGridLayout
{
    private const int MaxPreferredRowForExactPlacement = 10_000;

    public const int SmallGridColumnCount = 2;
    public const int StandardGridColumnCount = 4;
    public const int WideGridColumnCount = 6;
    public const double StandardGridBreakpoint = 600;
    public const double WideGridBreakpoint = 900;

    public static int SelectColumnCount(double effectiveWidth)
    {
        if (double.IsNaN(effectiveWidth) ||
            double.IsInfinity(effectiveWidth) ||
            effectiveWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveWidth));
        }

        return effectiveWidth < StandardGridBreakpoint
            ? SmallGridColumnCount
            : effectiveWidth < WideGridBreakpoint
                ? StandardGridColumnCount
                : WideGridColumnCount;
    }

    public static CardSpan GetSpan(CardSize size) => size switch
    {
        CardSize.S => new CardSpan(1, 1),
        CardSize.M => new CardSpan(2, 1),
        CardSize.L => new CardSpan(2, 2),
        CardSize.W => new CardSpan(4, 1),
        CardSize.XL => new CardSpan(4, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown card size."),
    };

    public static IReadOnlyList<CardPlacement> Pack(
        int columnCount,
        IReadOnlyList<CardLayoutItem> items)
    {
        ValidateColumnCount(columnCount);
        ArgumentNullException.ThrowIfNull(items);

        var occupiedRows = new List<bool[]>();
        var placements = new List<CardPlacement>(items.Count);
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (CardLayoutItem item in items)
        {
            ValidateItem(item, instanceIds);
            CardSpan requestedSpan = GetSpan(item.Size);
            int columnSpan = Math.Min(requestedSpan.Columns, columnCount);
            int maxColumn = columnCount - columnSpan;
            int? preferredColumn = item.PreferredColumn is int preferred
                ? Math.Clamp(preferred, 0, maxColumn)
                : null;

            int row = 0;
            int column = -1;
            bool restored = false;
            if (item.PreferredRow is int preferredRow &&
                preferredColumn is int preferredCellColumn &&
                preferredRow <= MaxPreferredRowForExactPlacement &&
                preferredRow <= int.MaxValue - requestedSpan.Rows)
            {
                EnsureRows(
                    occupiedRows,
                    preferredRow + requestedSpan.Rows,
                    columnCount);
                if (CanPlace(
                        occupiedRows,
                        preferredRow,
                        preferredCellColumn,
                        columnSpan,
                        requestedSpan.Rows))
                {
                    row = preferredRow;
                    column = preferredCellColumn;
                    restored = true;
                }
            }

            if (!restored)
            {
                row = 0;
                while (true)
                {
                    EnsureRows(occupiedRows, row + requestedSpan.Rows, columnCount);
                    column = FindColumn(
                        occupiedRows,
                        row,
                        columnCount,
                        columnSpan,
                        requestedSpan.Rows,
                        preferredColumn);
                    if (column >= 0)
                    {
                        break;
                    }

                    row++;
                }
            }

            MarkOccupied(
                occupiedRows,
                row,
                column,
                columnSpan,
                requestedSpan.Rows);
            placements.Add(
                new CardPlacement(
                    item.InstanceId,
                    item.Size,
                    column,
                    row,
                    columnSpan,
                    requestedSpan.Rows));
        }

        return placements;
    }

    private static int FindColumn(
        List<bool[]> occupiedRows,
        int row,
        int columnCount,
        int columnSpan,
        int rowSpan,
        int? preferredColumn)
    {
        int maxColumn = columnCount - columnSpan;
        foreach (int column in EnumerateColumns(maxColumn, preferredColumn))
        {
            if (CanPlace(occupiedRows, row, column, columnSpan, rowSpan))
            {
                return column;
            }
        }

        return -1;
    }

    private static bool CanPlace(
        List<bool[]> occupiedRows,
        int row,
        int column,
        int columnSpan,
        int rowSpan)
    {
        for (int rowOffset = 0; rowOffset < rowSpan; rowOffset++)
        {
            bool[] occupied = occupiedRows[row + rowOffset];
            for (int columnOffset = 0; columnOffset < columnSpan; columnOffset++)
            {
                if (occupied[column + columnOffset])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IEnumerable<int> EnumerateColumns(
        int maxColumn,
        int? preferredColumn)
    {
        if (preferredColumn is int preferred)
        {
            yield return preferred;
        }

        for (int column = 0; column <= maxColumn; column++)
        {
            if (column != preferredColumn)
            {
                yield return column;
            }
        }
    }

    private static void MarkOccupied(
        List<bool[]> occupiedRows,
        int row,
        int column,
        int columnSpan,
        int rowSpan)
    {
        for (int rowOffset = 0; rowOffset < rowSpan; rowOffset++)
        {
            bool[] occupied = occupiedRows[row + rowOffset];
            for (int columnOffset = 0; columnOffset < columnSpan; columnOffset++)
            {
                occupied[column + columnOffset] = true;
            }
        }
    }

    private static void EnsureRows(
        List<bool[]> occupiedRows,
        int requiredRowCount,
        int columnCount)
    {
        while (occupiedRows.Count < requiredRowCount)
        {
            occupiedRows.Add(new bool[columnCount]);
        }
    }

    private static void ValidateColumnCount(int columnCount)
    {
        if (columnCount is not (
            SmallGridColumnCount or
            StandardGridColumnCount or
            WideGridColumnCount))
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }
    }

    private static void ValidateItem(
        CardLayoutItem item,
        HashSet<string> instanceIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.InstanceId);
        if (!instanceIds.Add(item.InstanceId))
        {
            throw new ArgumentException(
                $"Duplicate card instance ID '{item.InstanceId}'.",
                nameof(item));
        }

        if (item.PreferredColumn is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(item),
                item.PreferredColumn,
                "Preferred column cannot be negative.");
        }

        if (item.PreferredRow is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(item),
                item.PreferredRow,
                "Preferred row cannot be negative.");
        }
    }
}
