using System.Globalization;
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

    /// <summary>
    /// What the crosshair calls the newest point. Prefixed with SysMonCurve rather than the
    /// curve element's own x:Uid: the resource loader reads every "Uid.Something" key as a
    /// property to set on that element, and a code-only key that collides with an x:Uid takes
    /// the panel down on open rather than at build time.
    /// </summary>
    public const string CurveNow = "SysMonCurve.Now";

    /// <summary>"{0} seconds ago", for a point inside the last minute.</summary>
    public const string CurveSecondsAgo = "SysMonCurve.SecondsAgo";

    /// <summary>"{0} minutes ago", once the offset has passed a minute.</summary>
    public const string CurveMinutesAgo = "SysMonCurve.MinutesAgo";

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
/// One sample of a reading's recent window, placed on 0..1 axes.
///
/// The level arrives already normalised from the broker, which is the only place it can be
/// done: a percentage is drawn against its own ceiling and a rate against the window's peak,
/// and that choice needs the raw numbers the panel never sees.
/// </summary>
public sealed record SystemMonitorCurvePoint
{
    /// <summary>Horizontal position, 0..1; the newest sample is always at 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Vertical position, 0..1, against whatever scale the broker chose.</summary>
    public double Level { get; init; }

    /// <summary>
    /// What the crosshair says here - the value as it was measured, and how long ago. Empty on
    /// a curve that is only a backdrop, which is every curve but the headline's.
    /// </summary>
    public string TipText { get; init; } = string.Empty;
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

    /// <summary>
    /// The recent window behind the value, oldest first. Empty until the machine has been
    /// sampled twice, and after every wake - the provider drops its window rather than drawing
    /// one line across a hole in time.
    /// </summary>
    public IReadOnlyList<SystemMonitorCurvePoint> CurvePoints { get; init; } =
        Array.Empty<SystemMonitorCurvePoint>();

    /// <summary>Localized wording for a metric this machine cannot currently report.</summary>
    public string MetricStatusText { get; init; } = string.Empty;

    public bool HasReading => string.Equals(
        Status,
        SystemMonitorMetricStatus.Ready,
        StringComparison.Ordinal);

    /// <summary>
    /// Two points make a line; one is a dot that says nothing about movement. A compact row is
    /// a single line of text with no room behind it.
    /// </summary>
    public bool IsCurveVisible =>
        HasReading &&
        CurvePoints.Count > 1 &&
        Detail != SystemMonitorDetail.Compact;

    /// <summary>
    /// Compact rows are one line, and a metric without a ceiling - a network rate - has no
    /// honest full scale to draw a bar against.
    ///
    /// The meter is what a row falls back to before it has a window to plot: the curve's last
    /// point is the same number the meter would show, so drawing both states one reading
    /// twice. That happens on the first tick of a cold start and after every wake, which is
    /// exactly when a row would otherwise be a number with nothing under it.
    /// </summary>
    public bool IsMeterVisible =>
        HasReading &&
        Ratio.HasValue &&
        Detail != SystemMonitorDetail.Compact &&
        !IsCurveVisible;

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

    /// <summary>
    /// The reading the card gives its headline to: the first one it can actually show. The
    /// list order is the user's own ranking, so this is their first choice, and it is the only
    /// row drawn large enough for the crosshair to have somewhere to stand.
    /// </summary>
    public SystemMonitorMetricRow? Headline => Metrics.Count > 0 ? Metrics[0] : null;

    /// <summary>Everything under the headline, in the user's order.</summary>
    public IReadOnlyList<SystemMonitorMetricRow> TrailingMetrics =>
        Metrics.Count > 1
            ? Metrics.Skip(1).ToArray()
            : Array.Empty<SystemMonitorMetricRow>();

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

        double intervalSeconds = ReadInterval(payload);
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
                    CurvePoints = ReadCurve(metric, intervalSeconds, resourceResolver),
                    MetricStatusText = Resolve(
                        resourceResolver,
                        ResolveStatusKey(status)),
                });
        }

        bool hasData = rows.Any(row => row.HasReading);
        return new SystemMonitorCardProjection(rows, hasData);
    }

    /// <summary>
    /// One reading's window as points on 0..1 axes. The newest sample is at 1, so every curve
    /// on the card ends at the same right edge - which is "now" - however many samples the
    /// window happens to hold; a short window after a wake draws a short line, not a stretched
    /// one.
    ///
    /// Labels are optional and only the headline carries them. When they are there but do not
    /// line up with the series, they are dropped rather than shifted: a value under the wrong
    /// point is worse than no crosshair text at all.
    /// </summary>
    private static SystemMonitorCurvePoint[] ReadCurve(
        JsonElement metric,
        double intervalSeconds,
        Func<string, string?> resourceResolver)
    {
        if (!metric.TryGetProperty("history", out JsonElement history) ||
            history.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SystemMonitorCurvePoint>();
        }

        int count = history.GetArrayLength();
        if (count < 2)
        {
            return Array.Empty<SystemMonitorCurvePoint>();
        }

        string[]? texts = ReadHistoryTexts(metric, count);
        var points = new SystemMonitorCurvePoint[count];
        int index = 0;
        foreach (JsonElement level in history.EnumerateArray())
        {
            double value = level.ValueKind == JsonValueKind.Number &&
                level.TryGetDouble(out double parsed) &&
                double.IsFinite(parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : 0d;
            points[index] = new SystemMonitorCurvePoint
            {
                Fraction = (double)index / (count - 1),
                Level = value,
                TipText = texts is null
                    ? string.Empty
                    : ComposeTipText(
                        texts[index],
                        count - 1 - index,
                        intervalSeconds,
                        resourceResolver),
            };
            index++;
        }

        return points;
    }

    private static string[]? ReadHistoryTexts(JsonElement metric, int count)
    {
        if (!metric.TryGetProperty("historyTexts", out JsonElement historyTexts) ||
            historyTexts.ValueKind != JsonValueKind.Array ||
            historyTexts.GetArrayLength() != count)
        {
            return null;
        }

        var texts = new string[count];
        int index = 0;
        foreach (JsonElement text in historyTexts.EnumerateArray())
        {
            texts[index++] = text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : string.Empty;
        }

        return texts;
    }

    /// <summary>
    /// The value and how long ago it was measured. The offset is counted in samples rather
    /// than clock time because that is what the series is: the broker states its own cadence,
    /// and a window with no cadence gets the value alone rather than an invented age.
    /// </summary>
    private static string ComposeTipText(
        string value,
        int samplesBack,
        double intervalSeconds,
        Func<string, string?> resourceResolver)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (intervalSeconds <= 0d)
        {
            return value;
        }

        string age;
        if (samplesBack == 0)
        {
            age = Resolve(resourceResolver, SystemMonitorResourceKeys.CurveNow);
        }
        else
        {
            int seconds = (int)Math.Round(samplesBack * intervalSeconds);
            age = seconds < 60
                ? Format(
                    resourceResolver,
                    SystemMonitorResourceKeys.CurveSecondsAgo,
                    seconds.ToString(CultureInfo.CurrentCulture))
                : Format(
                    resourceResolver,
                    SystemMonitorResourceKeys.CurveMinutesAgo,
                    (seconds / 60).ToString(CultureInfo.CurrentCulture));
        }

        return age.Length > 0 ? value + " · " + age : value;
    }

    private static double ReadInterval(JsonElement payload) =>
        payload.TryGetProperty("sampleIntervalSeconds", out JsonElement interval) &&
        interval.ValueKind == JsonValueKind.Number &&
        interval.TryGetDouble(out double seconds) &&
        double.IsFinite(seconds) &&
        seconds > 0d
            ? seconds
            : 0d;

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

    private static string Format(Func<string, string?> resolver, string key, string value)
    {
        string template = Resolve(resolver, key);
        return template.Contains("{0}", StringComparison.Ordinal)
            ? template.Replace("{0}", value, StringComparison.Ordinal)
            : template + " " + value;
    }
}
