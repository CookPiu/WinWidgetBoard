namespace WinWidgetBoard.Contracts.Protocol;

/// <summary>
/// The local hardware monitor. Metric identifiers are the one table shared by the broker, the
/// panel card and the native taskbar entry, the same way <see cref="WeatherConditionContract"/>
/// is shared: sending a raw counter name instead would make each of the three consumers keep
/// its own copy, and two of them would drift.
/// </summary>
public static class SystemMonitorContract
{
    public const string SettingsGetMethod = "sysmon.settings.get";
    public const string SettingsSaveMethod = "sysmon.settings.save";

    /// <summary>
    /// Read-only projection for callers that only need a short line of text and must not open
    /// a card subscription - today the native taskbar entry. The segments come back already
    /// composed, so the entry formats nothing and translates nothing.
    /// </summary>
    public const string SummaryGetMethod = "sysmon.summary.get";

    public const int MaxInstanceIdLength = CardsContract.MaxInstanceIdLength;
    public const string DefaultInstanceId = "demo.sysmon";

    /// <summary>
    /// Both lists are capped at the width the taskbar entry can physically show. A larger cap
    /// would only produce a configuration that cannot be rendered.
    /// </summary>
    public const int MaxItemsPerSurface = 8;

    public const int MaxSegmentTextLength = 32;

    /// <summary>
    /// How many recent samples travel with each taskbar segment for its sparkline. At the
    /// two-second cadence this is two minutes of history - long enough to show a spike you
    /// just missed, short enough that the summary stays a small message.
    /// </summary>
    public const int MaxHistorySamples = 60;

    // --- metric identifiers ---------------------------------------------------------------

    public const string CpuUsage = "cpu.usage";
    public const string CpuClock = "cpu.clock";
    public const string CpuTemperature = "cpu.temperature";
    public const string MemoryUsage = "memory.usage";
    public const string GpuUsage = "gpu.usage";
    public const string GpuMemory = "gpu.memory";
    public const string GpuTemperature = "gpu.temperature";
    public const string DiskActivity = "disk.activity";
    public const string DiskUsage = "disk.usage";
    public const string NetworkUp = "net.up";
    public const string NetworkDown = "net.down";
    public const string FanSpeed = "fan.speed";

    public static IReadOnlyList<string> MetricIds { get; } =
    [
        CpuUsage,
        CpuClock,
        CpuTemperature,
        MemoryUsage,
        GpuUsage,
        GpuMemory,
        GpuTemperature,
        DiskActivity,
        DiskUsage,
        NetworkUp,
        NetworkDown,
        FanSpeed,
    ];

    /// <summary>
    /// What the entry and the card show when nothing has been configured yet. Deliberately
    /// short on the entry: four segments is already most of the strip.
    /// </summary>
    public static IReadOnlyList<SystemMonitorItemDto> DefaultCardItems { get; } =
    [
        new() { MetricId = CpuUsage, Detail = SystemMonitorDetail.Detailed },
        new() { MetricId = MemoryUsage, Detail = SystemMonitorDetail.Detailed },
        new() { MetricId = GpuUsage, Detail = SystemMonitorDetail.Normal },
        new() { MetricId = NetworkDown, Detail = SystemMonitorDetail.Normal },
        new() { MetricId = NetworkUp, Detail = SystemMonitorDetail.Normal },
    ];

    public static IReadOnlyList<SystemMonitorItemDto> DefaultEntryItems { get; } =
    [
        new() { MetricId = CpuUsage, Detail = SystemMonitorDetail.Normal },
        new() { MetricId = MemoryUsage, Detail = SystemMonitorDetail.Normal },
        new() { MetricId = NetworkDown, Detail = SystemMonitorDetail.Compact },
        new() { MetricId = NetworkUp, Detail = SystemMonitorDetail.Compact },
    ];

    public static IReadOnlyList<string> Methods { get; } =
    [
        SettingsGetMethod,
        SettingsSaveMethod,
        SummaryGetMethod,
    ];

    public static bool IsValidInstanceId(string? value) =>
        CardsContract.IsValidIdentifier(value, MaxInstanceIdLength);

    public static bool IsKnownMetricId(string? value) =>
        value is not null && MetricIds.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Validates one surface's item list. Order is the list order, so no separate index is
    /// carried or checked; a repeated metric is rejected because the same reading twice is a
    /// configuration mistake rather than a layout the user could have wanted.
    /// </summary>
    public static bool IsValidItemList(IReadOnlyList<SystemMonitorItemDto>? items)
    {
        if (items is null || items.Count > MaxItemsPerSurface)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SystemMonitorItemDto item in items)
        {
            if (item is null ||
                !IsKnownMetricId(item.MetricId) ||
                !Enum.IsDefined(item.Detail) ||
                !seen.Add(item.MetricId!))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// How much of a metric to show. The two surfaces read it differently because they have
/// different room: the card adds a name and a meter, the entry adds a language-neutral tag
/// and then a secondary value.
/// </summary>
public enum SystemMonitorDetail
{
    /// <summary>Card: value only. Entry: glyph and value.</summary>
    Compact,

    /// <summary>Card: name, value and meter. Entry: glyph, short tag and value.</summary>
    Normal,

    /// <summary>Card and entry both add the metric's secondary reading.</summary>
    Detailed,
}

public static class SystemMonitorMetricStatus
{
    public const string Ready = "ready";

    /// <summary>
    /// The reading exists but has no baseline yet - the first sample after the provider woke
    /// from its idle state cannot produce a delta. The surfaces show a placeholder for one
    /// tick rather than a zero that looks like a real measurement.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>
    /// This machine cannot supply the reading at all. Temperature, clock and fan land here
    /// until the sensor service is installed.
    /// </summary>
    public const string Unavailable = "unavailable";
}

public sealed record SystemMonitorItemDto
{
    public string? MetricId { get; init; }

    public SystemMonitorDetail Detail { get; init; } = SystemMonitorDetail.Normal;
}

public sealed record SystemMonitorSettingsGetRequest
{
    public string? InstanceId { get; init; }
}

public sealed record SystemMonitorSettingsGetResponse
{
    public SystemMonitorSettingsDto Settings { get; init; } = new();
}

public sealed record SystemMonitorSettingsSaveRequest
{
    public Guid ClientOperationId { get; init; }

    public string? InstanceId { get; init; }

    public IReadOnlyList<SystemMonitorItemDto>? CardItems { get; init; }

    public IReadOnlyList<SystemMonitorItemDto>? EntryItems { get; init; }

    public int ExpectedRevision { get; init; }
}

public sealed record SystemMonitorSettingsSaveResponse
{
    public Guid ClientOperationId { get; init; }

    public SystemMonitorSettingsDto Settings { get; init; } = new();
}

public sealed record SystemMonitorSettingsDto
{
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>What the card shows, in display order.</summary>
    public IReadOnlyList<SystemMonitorItemDto> CardItems { get; init; } =
        Array.Empty<SystemMonitorItemDto>();

    /// <summary>
    /// What the taskbar entry shows, in display order. Kept separate from
    /// <see cref="CardItems"/> because the strip has room for a few values and the card has
    /// room for all of them; one shared list would always be cramped on one side.
    /// </summary>
    public IReadOnlyList<SystemMonitorItemDto> EntryItems { get; init; } =
        Array.Empty<SystemMonitorItemDto>();

    public int Revision { get; init; }

    public string UpdatedAtUtc { get; init; } = string.Empty;
}

/// <summary>
/// The card snapshot payload. Every string is final: the broker owns units and rounding so
/// the panel binds text instead of reaching into numbers, the same discipline
/// <see cref="WeatherSummaryDto"/> applies to the entry. The metric name itself is not here -
/// that one string is localized, and the panel owns its resources.
/// </summary>
public sealed record SystemMonitorCardPayloadDto
{
    public IReadOnlyList<SystemMonitorMetricDto> Metrics { get; init; } =
        Array.Empty<SystemMonitorMetricDto>();

    public string SampledAtUtc { get; init; } = string.Empty;
}

public sealed record SystemMonitorMetricDto
{
    public string MetricId { get; init; } = string.Empty;

    public string IconId { get; init; } = string.Empty;

    public string Status { get; init; } = SystemMonitorMetricStatus.Unavailable;

    public SystemMonitorDetail Detail { get; init; } = SystemMonitorDetail.Normal;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    /// <summary>
    /// 0..1 for the card's meter, or null for a metric with no meaningful full scale - a
    /// network rate has no ceiling to draw a bar against.
    /// </summary>
    public double? Ratio { get; init; }
}

public sealed record SystemMonitorSummaryGetRequest
{
    public string? InstanceId { get; init; }
}

public sealed record SystemMonitorSummaryGetResponse
{
    /// <summary>
    /// Absent until the first sample of this process. Callers render their own unavailable
    /// state rather than a stale or invented value.
    /// </summary>
    public SystemMonitorSummaryDto? Summary { get; init; }
}

public sealed record SystemMonitorSummaryDto
{
    public string InstanceId { get; init; } = string.Empty;

    public IReadOnlyList<SystemMonitorSegmentDto> Segments { get; init; } =
        Array.Empty<SystemMonitorSegmentDto>();

    public string SampledAtUtc { get; init; } = string.Empty;
}

/// <summary>
/// One already-composed piece of the taskbar entry. <see cref="Text"/> is final: the entry
/// neither formats numbers nor translates anything, exactly as it does not for weather.
/// </summary>
public sealed record SystemMonitorSegmentDto
{
    public string MetricId { get; init; } = string.Empty;

    /// <summary>A glyph token the entry draws; see the metric identifiers above.</summary>
    public string IconId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Recent samples for this reading, oldest first, already normalised to 0..1 and capped at
    /// <see cref="SystemMonitorContract.MaxHistorySamples"/>. The broker normalises for the
    /// same reason it formats the text: a percentage has a fixed 0..100 scale while a network
    /// rate has none and can only be drawn against the window's own peak, and that choice
    /// needs the raw numbers the entry never sees. Empty when the reading is unavailable or
    /// has not been sampled twice yet.
    /// </summary>
    public IReadOnlyList<double> History { get; init; } = Array.Empty<double>();
}
