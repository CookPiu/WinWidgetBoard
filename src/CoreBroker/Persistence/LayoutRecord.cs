namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed record LayoutItemRecord(
    string InstanceId,
    int OrderIndex,
    int ColumnSpan,
    int RowSpan,
    string SizeId,
    int? PreferredColumn,
    int? PreferredRow = null);

public sealed record LayoutRecord(
    string LayoutId,
    string DisplayId,
    int Revision,
    IReadOnlyList<LayoutItemRecord> Items,
    string UpdatedAtUtc);

public sealed class LayoutRevisionConflictException : InvalidOperationException
{
    public LayoutRevisionConflictException(
        string layoutId,
        int expectedRevision,
        int actualRevision)
        : base($"The layout '{layoutId}' changed since the expected revision was read.")
    {
        LayoutId = layoutId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string LayoutId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}
