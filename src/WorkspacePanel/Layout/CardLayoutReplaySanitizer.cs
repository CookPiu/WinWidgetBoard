namespace WinWidgetBoard.WorkspacePanel.Layout;

public readonly record struct CardLayoutReplayResult(
    IReadOnlyList<CardLayoutItem> Items,
    int RecoveredItemCount);

public static class CardLayoutReplaySanitizer
{
    private const int MaxAdditionalRowsBeyondCompactLayout = 2;

    public static CardLayoutReplayResult Normalize(
        int columnCount,
        IReadOnlyList<CardLayoutItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CardLayoutItem[] compactInput = items
            .Select(item => item with { PreferredRow = null })
            .ToArray();
        IReadOnlyList<CardPlacement> compactPlacements =
            ResponsiveGridLayout.Pack(columnCount, compactInput);
        int compactBottomExclusive = compactPlacements.Count == 0
            ? 0
            : compactPlacements.Max(placement => placement.BottomExclusive);
        int maximumReplayRow = compactBottomExclusive >
            int.MaxValue - MaxAdditionalRowsBeyondCompactLayout
                ? int.MaxValue
                : compactBottomExclusive +
                    MaxAdditionalRowsBeyondCompactLayout;

        int recoveredItemCount = 0;
        CardLayoutItem[] normalized = items
            .Select(item =>
            {
                if (item.PreferredRow is not int preferredRow ||
                    preferredRow <= maximumReplayRow)
                {
                    return item;
                }

                recoveredItemCount++;
                return item with { PreferredRow = null };
            })
            .ToArray();

        return new CardLayoutReplayResult(
            Array.AsReadOnly(normalized),
            recoveredItemCount);
    }
}
