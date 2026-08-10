namespace WinWidgetBoard.WorkspacePanel.Layout;

public static class CardGridRealizationPlanner
{
    public static IReadOnlyList<int> GetRealizedIndices(
        IReadOnlyList<CardPlacement> placements,
        double rowHeight,
        double rowGap,
        double realizationTop,
        double realizationHeight,
        int recommendedAnchorIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (!double.IsFinite(rowHeight) || rowHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowHeight),
                rowHeight,
                "Row height must be finite and positive.");
        }
        if (!double.IsFinite(rowGap) || rowGap < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowGap),
                rowGap,
                "Row gap must be finite and non-negative.");
        }
        if (placements.Count == 0)
        {
            return [];
        }

        double realizationBottom = realizationTop + realizationHeight;
        if (!double.IsFinite(realizationTop) ||
            !double.IsFinite(realizationHeight) ||
            realizationHeight <= 0 ||
            !double.IsFinite(realizationBottom))
        {
            return Enumerable.Range(0, placements.Count).ToArray();
        }

        var indices = new List<int>();
        for (int index = 0; index < placements.Count; index++)
        {
            CardPlacement placement = placements[index];
            double itemTop = placement.Row * (rowHeight + rowGap);
            double itemBottom = itemTop +
                placement.RowSpan * rowHeight +
                (placement.RowSpan - 1) * rowGap;
            if (itemBottom > realizationTop && itemTop < realizationBottom)
            {
                indices.Add(index);
            }
        }

        if (recommendedAnchorIndex >= 0 &&
            recommendedAnchorIndex < placements.Count &&
            !indices.Contains(recommendedAnchorIndex))
        {
            indices.Add(recommendedAnchorIndex);
            indices.Sort();
        }

        return indices;
    }
}
