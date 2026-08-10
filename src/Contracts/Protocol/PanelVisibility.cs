namespace WinWidgetBoard.Contracts.Protocol;

public static class PanelVisibilityContract
{
    public const string Method = "panel.report-visibility";

    public const int MaxDisplayIdLength = 256;
}

public sealed record PanelVisibilityReportRequest
{
    public Guid ClientOperationId { get; init; }

    public bool PanelVisible { get; init; }

    public string? DisplayId { get; init; }
}

public sealed record PanelVisibilityReportResponse
{
    public Guid ClientOperationId { get; init; }

    public bool PanelVisible { get; init; }

    public long Revision { get; init; }

    public DateTimeOffset AcceptedAtUtc { get; init; }
}
