namespace WinWidgetBoard.Contracts.Protocol;

public static class LayoutContract
{
    public const string GetMethod = "layout.get";
    public const string SaveMethod = "layout.save";

    public const int MaxLayoutIdLength = 128;
    public const int MaxDisplayIdLength = 256;
    public const int MaxInstanceIdLength = 200;
    public const int MaxSizeIdLength = 8;
    public const int MaxItems = 100;

    public static IReadOnlyList<string> Methods { get; } =
    [
        GetMethod,
        SaveMethod,
    ];

    public static bool TryGetDeclaredSpan(
        string? sizeId,
        out int columnSpan,
        out int rowSpan)
    {
        (columnSpan, rowSpan) = sizeId switch
        {
            "s" => (1, 1),
            "m" => (2, 1),
            "l" => (2, 2),
            "w" => (4, 1),
            "xl" => (4, 2),
            _ => (0, 0),
        };
        return columnSpan > 0;
    }
}

public sealed record LayoutGetRequest
{
    public string? LayoutId { get; init; }

    public string? DisplayId { get; init; }
}

public sealed record LayoutGetResponse
{
    public LayoutDto? Layout { get; init; }
}

public sealed record LayoutSaveRequest
{
    public Guid ClientOperationId { get; init; }

    public string? LayoutId { get; init; }

    public string? DisplayId { get; init; }

    public int ExpectedRevision { get; init; }

    public IReadOnlyList<LayoutItemDto> Items { get; init; } = Array.Empty<LayoutItemDto>();
}

public sealed record LayoutSaveResponse
{
    public Guid ClientOperationId { get; init; }

    public LayoutDto Layout { get; init; } = new();
}

public sealed record LayoutDto
{
    public string LayoutId { get; init; } = string.Empty;

    public string DisplayId { get; init; } = string.Empty;

    public int Revision { get; init; }

    public int SmallColumns { get; init; } = 2;

    public int NormalColumns { get; init; } = 4;

    public int WideColumns { get; init; } = 6;

    public IReadOnlyList<LayoutItemDto> Items { get; init; } = Array.Empty<LayoutItemDto>();

    public string UpdatedAtUtc { get; init; } = string.Empty;
}

public sealed record LayoutItemDto
{
    public string InstanceId { get; init; } = string.Empty;

    public int Order { get; init; }

    public int ColumnSpan { get; init; }

    public int RowSpan { get; init; }

    public string SizeId { get; init; } = string.Empty;

    public int? PreferredColumn { get; init; }

    public int? PreferredRow { get; init; }
}
