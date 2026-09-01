using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Resource keys for the metric names and the non-ready states. The broker formats every
/// number but never a name: names are the one part that has to be translated, and the panel
/// owns the resources.
/// </summary>
public static class SystemMonitorResourceKeys
{
    public const string PendingStatus = "SysMonStatus.Pending";
    public const string UnavailableStatus = "SysMonStatus.Unavailable";

    public static string GetNameKey(string metricId) => metricId switch
    {
        SystemMonitorContract.CpuUsage => "SysMonMetric.CpuUsage",
        SystemMonitorContract.CpuClock => "SysMonMetric.CpuClock",
        SystemMonitorContract.CpuTemperature => "SysMonMetric.CpuTemperature",
        SystemMonitorContract.MemoryUsage => "SysMonMetric.MemoryUsage",
        SystemMonitorContract.GpuUsage => "SysMonMetric.GpuUsage",
        SystemMonitorContract.GpuMemory => "SysMonMetric.GpuMemory",
        SystemMonitorContract.GpuTemperature => "SysMonMetric.GpuTemperature",
        SystemMonitorContract.DiskActivity => "SysMonMetric.DiskActivity",
        SystemMonitorContract.DiskUsage => "SysMonMetric.DiskUsage",
        SystemMonitorContract.NetworkUp => "SysMonMetric.NetworkUp",
        SystemMonitorContract.NetworkDown => "SysMonMetric.NetworkDown",
        SystemMonitorContract.FanSpeed => "SysMonMetric.FanSpeed",
        _ => "SysMonMetric.Unknown",
    };
}

/// <summary>
/// One row of the hardware monitor card. The visibility flags are resolved here rather than in
/// XAML so the rules - a meter only where a ratio has a real ceiling, a secondary line only at
/// the detailed level - are testable without a WinUI host.
/// </summary>
public sealed record SystemMonitorMetricRow
{
    public string MetricId { get; init; } = string.Empty;

    public string IconId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = SystemMonitorMetricStatus.Unavailable;

    public SystemMonitorDetail Detail { get; init; } = SystemMonitorDetail.Normal;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public double? Ratio { get; init; }

    /// <summary>Localized wording for a metric this machine cannot currently report.</summary>
    public string MetricStatusText { get; init; } = string.Empty;

    public bool HasReading => string.Equals(
        Status,
        SystemMonitorMetricStatus.Ready,
        StringComparison.Ordinal);

    /// <summary>
    /// Compact rows are one line, and a metric without a ceiling - a network rate - has no
    /// honest full scale to draw a bar against.
    /// </summary>
    public bool IsMeterVisible =>
        HasReading && Ratio.HasValue && Detail != SystemMonitorDetail.Compact;

    public bool IsSecondaryVisible =>
        HasReading &&
        Detail == SystemMonitorDetail.Detailed &&
        SecondaryText.Length > 0;

    /// <summary>Shown in place of the secondary line when there is no reading.</summary>
    public bool IsMetricStatusTextVisible => !HasReading;

    public double MeterFraction => Ratio ?? 0d;

    /// <summary>
    /// Screen readers get the name and the value together; the meter is decoration over the
    /// same number and carries no separate automation name.
    /// </summary>
    public string AutomationName => HasReading
        ? IsSecondaryVisible
            ? $"{Name} {PrimaryText} {SecondaryText}"
            : $"{Name} {PrimaryText}"
        : $"{Name} {MetricStatusText}";
}

public sealed record SystemMonitorCardProjection
{
    private SystemMonitorCardProjection(
        IReadOnlyList<SystemMonitorMetricRow> metrics,
        bool hasData)
    {
        Metrics = metrics;
        HasData = hasData;
    }

    public IReadOnlyList<SystemMonitorMetricRow> Metrics { get; }

    public bool HasData { get; }

    public static SystemMonitorCardProjection Empty { get; } =
        new(Array.Empty<SystemMonitorMetricRow>(), hasData: false);

    public static SystemMonitorCardProjection FromSnapshot(
        CardRuntimeSnapshot snapshot,
        Func<string, string?> resourceResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resourceResolver);

        JsonElement payload = snapshot.Payload;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("metrics", out JsonElement metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            return Empty;
        }

        var rows = new List<SystemMonitorMetricRow>(metrics.GetArrayLength());
        foreach (JsonElement metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? metricId = ReadString(metric, "metricId");
            if (metricId is null || !SystemMonitorContract.IsKnownMetricId(metricId))
            {
                // A newer broker can report a metric this build has no name for. Dropping it
                // is better than showing a value nobody can label.
                continue;
            }

            string status = ReadString(metric, "status") ??
                SystemMonitorMetricStatus.Unavailable;
            if (string.Equals(
                    status,
                    SystemMonitorMetricStatus.NeedsSensorSource,
                    StringComparison.Ordinal))
            {
                // A reading held up by the optional sensor source stays off the card: the
                // row would only ever repeat a setup instruction, and an instruction is
                // settings content, not monitoring content. The settings page states the
                // requirement next to the metric lists instead.
                continue;
            }

            rows.Add(
                new SystemMonitorMetricRow
                {
                    MetricId = metricId,
                    IconId = ReadString(metric, "iconId") ?? "unknown",
                    Name = Resolve(
                        resourceResolver,
                        SystemMonitorResourceKeys.GetNameKey(metricId)),
                    Status = status,
                    Detail = ReadDetail(metric),
                    PrimaryText = ReadString(metric, "primaryText") ?? "—",
                    SecondaryText = ReadString(metric, "secondaryText") ?? string.Empty,
                    Ratio = ReadRatio(metric),
                    MetricStatusText = Resolve(
                        resourceResolver,
                        ResolveStatusKey(status)),
                });
        }

        bool hasData = rows.Any(row => row.HasReading);
        return new SystemMonitorCardProjection(rows, hasData);
    }

    /// <summary>Which wording a non-ready row carries.</summary>
    private static string ResolveStatusKey(string status) => status switch
    {
        SystemMonitorMetricStatus.Pending => SystemMonitorResourceKeys.PendingStatus,
        _ => SystemMonitorResourceKeys.UnavailableStatus,
    };

    private static SystemMonitorDetail ReadDetail(JsonElement metric)
    {
        string? detail = ReadString(metric, "detail");
        return Enum.TryParse(detail, ignoreCase: true, out SystemMonitorDetail parsed) &&
            Enum.IsDefined(parsed)
            ? parsed
            : SystemMonitorDetail.Normal;
    }

    private static double? ReadRatio(JsonElement metric)
    {
        if (!metric.TryGetProperty("ratio", out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !value.TryGetDouble(out double ratio) ||
            !double.IsFinite(ratio))
        {
            return null;
        }

        return Math.Clamp(ratio, 0d, 1d);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Resolve(Func<string, string?> resolver, string key)
    {
        string? value = resolver(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }
}
