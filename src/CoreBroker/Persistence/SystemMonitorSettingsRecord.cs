using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Which readings the card and the taskbar entry show, in display order. No measurement is
/// persisted - only the user's choice of what to look at.
/// </summary>
public sealed record SystemMonitorSettingsRecord
{
    public SystemMonitorSettingsRecord(
        string instanceId,
        IReadOnlyList<SystemMonitorItemDto> cardItems,
        IReadOnlyList<SystemMonitorItemDto> entryItems,
        int revision,
        string? updatedAtUtc)
    {
        if (!SystemMonitorContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "System monitor settings instance ID is invalid.",
                nameof(instanceId));
        }

        if (!SystemMonitorContract.IsValidItemList(cardItems))
        {
            throw new ArgumentException(
                "System monitor card items are invalid.",
                nameof(cardItems));
        }

        if (!SystemMonitorContract.IsValidItemList(entryItems))
        {
            throw new ArgumentException(
                "System monitor entry items are invalid.",
                nameof(entryItems));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (updatedAtUtc is not null)
        {
            updatedAtUtc = NoteRecord.FormatTimestamp(
                NoteRecord.ParseTimestamp(updatedAtUtc, nameof(updatedAtUtc)));
        }

        InstanceId = instanceId;
        CardItems = cardItems.ToArray();
        EntryItems = entryItems.ToArray();
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string InstanceId { get; }

    public IReadOnlyList<SystemMonitorItemDto> CardItems { get; }

    public IReadOnlyList<SystemMonitorItemDto> EntryItems { get; }

    public int Revision { get; }

    public string? UpdatedAtUtc { get; }

    public static string SerializeItems(IReadOnlyList<SystemMonitorItemDto> items) =>
        JsonSerializer.Serialize(items, ContractJson.Options);

    /// <summary>
    /// Reads a stored list back. A row written by a newer build can name a metric this one
    /// does not have; the unknown entries are dropped rather than failing the read, so an
    /// older build still starts and still shows everything it does understand.
    /// </summary>
    public static IReadOnlyList<SystemMonitorItemDto> DeserializeItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemMonitorItemDto>();
        }

        List<SystemMonitorItemDto>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<SystemMonitorItemDto>>(
                json,
                ContractJson.Options);
        }
        catch (JsonException)
        {
            return Array.Empty<SystemMonitorItemDto>();
        }

        if (parsed is null)
        {
            return Array.Empty<SystemMonitorItemDto>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<SystemMonitorItemDto>(parsed.Count);
        foreach (SystemMonitorItemDto item in parsed)
        {
            if (item?.MetricId is null ||
                !SystemMonitorContract.IsKnownMetricId(item.MetricId) ||
                !Enum.IsDefined(item.Detail) ||
                !seen.Add(item.MetricId) ||
                items.Count >= SystemMonitorContract.MaxItemsPerSurface)
            {
                continue;
            }

            items.Add(item);
        }

        return items;
    }
}

public sealed class SystemMonitorSettingsRevisionConflictException : InvalidOperationException
{
    public SystemMonitorSettingsRevisionConflictException(
        string instanceId,
        int expectedRevision,
        int actualRevision)
        : base(
            $"System monitor settings '{instanceId}' changed since the expected revision " +
            "was read.")
    {
        InstanceId = instanceId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string InstanceId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}
