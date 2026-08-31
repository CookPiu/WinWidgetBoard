namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// Turns a resize drag into one of the five card sizes.
///
/// The gesture is direct manipulation, but the model underneath is not continuous: layout
/// persists a size id and an order, never pixels (ADR 0003 / 0014). So the pointer picks a
/// number of columns and rows, and this snaps that pair to the nearest size that actually
/// exists. Dragging therefore feels like resizing a picture and still cannot produce a card
/// the grid has no way to store.
///
/// Only the east, south and south-east handles exist, so a resize never moves the card's
/// origin: placement comes from first-fit packing, and a card that grew from its top-left
/// would ask the packer to honour an origin it may immediately re-pack away.
/// </summary>
public static class CardResizeCalculator
{
    private const double FallbackCellWidth = 180;

    /// <summary>Column spans a card can actually have: S/M are 1 and 2, W/XL are 4.</summary>
    private static readonly int[] ColumnSteps = [1, 2, 4];

    /// <summary>The tallest card is two rows; there is no three-row size.</summary>
    private const int MaxRowSpan = 2;

    public static CardSize GetResizeTarget(
        int columnCount,
        double availableWidth,
        double columnGap,
        double rowHeight,
        double rowGap,
        CardPlacement placement,
        double offsetX,
        double offsetY,
        bool resizeColumns,
        bool resizeRows)
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
            : columnCount * FallbackCellWidth + (columnCount - 1) * columnGap;
        double cellWidth = Math.Max(
            0,
            (width - (columnCount - 1) * columnGap) / columnCount);
        double columnStride = cellWidth + columnGap;
        double rowStride = rowHeight + rowGap;

        int requestedColumns = placement.ColumnSpan;
        if (resizeColumns && columnStride > 0 && double.IsFinite(offsetX))
        {
            // The card's current width in DIP, plus how far the handle moved, read back as a
            // span. The trailing gap is added before dividing because a span of n covers
            // n strides minus the one gap that follows the last column.
            double currentWidth = (placement.ColumnSpan * columnStride) - columnGap;
            requestedColumns = (int)Math.Round(
                (currentWidth + offsetX + columnGap) / columnStride,
                MidpointRounding.AwayFromZero);
        }

        int requestedRows = placement.RowSpan;
        if (resizeRows && rowStride > 0 && double.IsFinite(offsetY))
        {
            double currentHeight = (placement.RowSpan * rowStride) - rowGap;
            requestedRows = (int)Math.Round(
                (currentHeight + offsetY + rowGap) / rowStride,
                MidpointRounding.AwayFromZero);
        }

        return Snap(requestedColumns, requestedRows, columnCount);
    }

    /// <summary>
    /// The nearest size to a requested span. A tie goes to the narrower card: shrinking is
    /// the correction a user makes after overshooting, so it has to be the easier direction
    /// to land on.
    /// </summary>
    public static CardSize Snap(int columns, int rows, int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);
        int rowSpan = Math.Clamp(rows, 1, MaxRowSpan);
        int columnSpan = SnapColumns(columns, columnCount);

        // There is no one-column two-row size. A card dragged taller from S therefore becomes
        // L rather than refusing to grow, which is what the grid can actually hold.
        if (columnSpan == 1 && rowSpan == MaxRowSpan)
        {
            columnSpan = columnCount >= 2 ? 2 : 1;
            if (columnSpan == 1)
            {
                rowSpan = 1;
            }
        }

        return (columnSpan, rowSpan) switch
        {
            (1, 1) => CardSize.S,
            (2, 1) => CardSize.M,
            (4, 1) => CardSize.W,
            (2, 2) => CardSize.L,
            (4, 2) => CardSize.XL,
            _ => CardSize.M,
        };
    }

    private static int SnapColumns(int columns, int columnCount)
    {
        int best = 1;
        int bestDistance = int.MaxValue;
        foreach (int step in ColumnSteps)
        {
            // A grid narrower than the step cannot show it: Pack would clamp the span, and a
            // card the user cannot see at its stated size is worse than one that refuses to
            // grow past the board.
            if (step > columnCount)
            {
                continue;
            }

            int distance = Math.Abs(step - columns);
            if (distance < bestDistance)
            {
                best = step;
                bestDistance = distance;
            }
        }

        return best;
    }
}
