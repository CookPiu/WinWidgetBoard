using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardGridLayout : VirtualizingLayout
{
    private const double DefaultColumnGap = 12;
    private const double DefaultRowGap = 12;
    private const double DefaultRowHeight = 180;
    private const double FallbackCellWidth = 180;
    private int _columnCount = 4;

    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            if (value is not (2 or 4 or 6))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Card grid column count must be 2, 4, or 6.");
            }

            if (_columnCount == value)
            {
                return;
            }

            _columnCount = value;
            InvalidateMeasure();
        }
    }

    public double ColumnGap { get; set; } = DefaultColumnGap;

    public double RowGap { get; set; } = DefaultRowGap;

    public double RowHeight { get; set; } = DefaultRowHeight;

    public void InvalidatePlacements()
    {
        InvalidateMeasure();
    }

    public GridCell GetDropCell(
        double availableWidth,
        CardPlacement placement,
        double offsetX,
        double offsetY) =>
        CardGridDropTargetCalculator.GetDropCell(
            _columnCount,
            availableWidth,
            ColumnGap,
            RowHeight,
            RowGap,
            placement,
            offsetX,
            offsetY);

    protected override Size MeasureOverride(
        VirtualizingLayoutContext context,
        Size availableSize)
    {
        double width = ResolveWidth(availableSize.Width);
        double cellWidth = GetCellWidth(width);
        double contentHeight = GetContentHeight(context);

        for (int index = 0; index < context.ItemCount; index++)
        {
            CardPlacement placement = GetPlacement(context, index);
            UIElement element = context.GetOrCreateElementAt(index);
            element.Measure(new Size(
                GetSpanWidth(cellWidth, placement.ColumnSpan),
                GetSpanHeight(placement.RowSpan)));
        }

        return new Size(width, contentHeight);
    }

    protected override Size ArrangeOverride(
        VirtualizingLayoutContext context,
        Size finalSize)
    {
        double width = ResolveWidth(finalSize.Width);
        double cellWidth = GetCellWidth(width);

        for (int index = 0; index < context.ItemCount; index++)
        {
            CardPlacement placement = GetPlacement(context, index);
            UIElement element = context.GetOrCreateElementAt(index);
            double x = placement.Column * (cellWidth + ColumnGap);
            double y = placement.Row * (RowHeight + RowGap);
            element.Arrange(new Rect(
                x,
                y,
                GetSpanWidth(cellWidth, placement.ColumnSpan),
                GetSpanHeight(placement.RowSpan)));
        }

        return new Size(width, GetContentHeight(context));
    }

    private double ResolveWidth(double availableWidth)
    {
        if (double.IsFinite(availableWidth) && availableWidth > 0)
        {
            return availableWidth;
        }

        return _columnCount * FallbackCellWidth +
            (_columnCount - 1) * ColumnGap;
    }

    private double GetCellWidth(double width)
    {
        return Math.Max(
            0,
            (width - (_columnCount - 1) * ColumnGap) / _columnCount);
    }

    private double GetSpanWidth(double cellWidth, int columnSpan)
    {
        return cellWidth * columnSpan + (columnSpan - 1) * ColumnGap;
    }

    private double GetSpanHeight(int rowSpan)
    {
        return RowHeight * rowSpan + (rowSpan - 1) * RowGap;
    }

    private double GetContentHeight(VirtualizingLayoutContext context)
    {
        int bottomRowExclusive = 0;
        for (int index = 0; index < context.ItemCount; index++)
        {
            bottomRowExclusive = Math.Max(
                bottomRowExclusive,
                GetPlacement(context, index).BottomExclusive);
        }

        return bottomRowExclusive == 0
            ? 0
            : bottomRowExclusive * RowHeight +
                (bottomRowExclusive - 1) * RowGap;
    }

    private static CardPlacement GetPlacement(
        VirtualizingLayoutContext context,
        int index)
    {
        return context.GetItemAt(index) is CardSurfaceItem item
            ? item.Placement
            : throw new InvalidOperationException(
                "CardGridLayout requires CardSurfaceItem data.");
    }
}
