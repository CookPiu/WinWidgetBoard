using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// A reading as a number rather than as text. <see cref="FullScale"/> is the value that means
/// "100% of this metric", or null when the metric has no ceiling at all - a network rate can
/// only be judged against the other rates around it.
/// </summary>
public readonly record struct SystemMonitorMagnitude(double Value, double? FullScale);

/// <summary>
/// Turns raw readings into the exact strings the card and the taskbar entry display. The broker
/// owns units and rounding on purpose: the entry is a layered Win32 window with no formatting or
/// localization of its own, and the card should bind text rather than re-derive numbers.
///
/// Everything here is pure - no Win32, no clock, no state - so the whole formatting surface is
/// unit-testable without a machine that happens to have a GPU.
/// </summary>
public static class SystemMonitorFormatter
{
    /// <summary>Shown wherever a reading exists in principle but not right now.</summary>
    public const string Placeholder = "—";

    /// <summary>
    /// Metrics derived from a difference between two ticks. Only these can be "pending": the
    /// others either have a value or genuinely cannot be read on this machine.
    /// </summary>
    private static readonly HashSet<string> RateMetrics = new(StringComparer.Ordinal)
    {
        SystemMonitorContract.CpuUsage,
        // The clock is a ratio the counters accumulate between two collections, so it has the
        // same first-tick gap the usage readings do.
        SystemMonitorContract.CpuClock,
        SystemMonitorContract.GpuUsage,
        SystemMonitorContract.GpuMemory,
        SystemMonitorContract.DiskActivity,
        SystemMonitorContract.NetworkUp,
        SystemMonitorContract.NetworkDown,
    };

    /// <summary>
    /// Metrics that have no user-mode API at all and come from the optional sensor source. Only
    /// these can report "needs sensor source": every other metric that has no value has none
    /// because this machine does not expose it, which running HWiNFO would not change.
    /// </summary>
    private static readonly HashSet<string> SensorSourceMetrics = new(StringComparer.Ordinal)
    {
        SystemMonitorContract.CpuTemperature,
        SystemMonitorContract.GpuTemperature,
        SystemMonitorContract.FanSpeed,
    };

    public static string ResolveIconId(string metricId) => metricId switch
    {
        SystemMonitorContract.CpuUsage or
        SystemMonitorContract.CpuClock or
        SystemMonitorContract.CpuTemperature => "cpu",
        SystemMonitorContract.MemoryUsage => "memory",
        SystemMonitorContract.GpuUsage or
        SystemMonitorContract.GpuMemory or
        SystemMonitorContract.GpuTemperature => "gpu",
        SystemMonitorContract.DiskActivity or
        SystemMonitorContract.DiskUsage => "disk",
        SystemMonitorContract.NetworkUp => "net-up",
        SystemMonitorContract.NetworkDown => "net-down",
        SystemMonitorContract.FanSpeed => "fan",
        _ => "unknown",
    };

    /// <summary>
    /// The entry's short tag. These are the same in every language the panel ships, which is
    /// what keeps the taskbar entry free of translated text. The network arrows carry their own
    /// direction in the glyph, so they add no tag at all.
    /// </summary>
    public static string ResolveShortTag(string metricId) => metricId switch
    {
        SystemMonitorContract.CpuUsage => "CPU",
        SystemMonitorContract.CpuClock => "CPU",
        SystemMonitorContract.CpuTemperature => "CPU",
        SystemMonitorContract.MemoryUsage => "MEM",
        SystemMonitorContract.GpuUsage => "GPU",
        SystemMonitorContract.GpuMemory => "VRAM",
        SystemMonitorContract.GpuTemperature => "GPU",
        SystemMonitorContract.DiskActivity => "DISK",
        SystemMonitorContract.DiskUsage => "DISK",
        SystemMonitorContract.FanSpeed => "FAN",
        _ => string.Empty,
    };

    /// <summary>
    /// One reading's raw magnitude and the full scale it should be drawn against, or null when
    /// this machine cannot supply it. A percentage has a fixed 0..100 scale and a used-of-total
    /// reading has its own ceiling; a rate has neither, and reports a null scale so the caller
    /// knows it can only be drawn relative to the other samples around it.
    ///
    /// This exists for the sparkline. The formatted strings above cannot be plotted, and the
    /// entry never sees the numbers, so the broker is the only place that can make the choice.
    /// </summary>
    public static SystemMonitorMagnitude? TryGetMagnitude(
        SystemMetricSample sample,
        string metricId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(metricId);

        return metricId switch
        {
            SystemMonitorContract.CpuUsage => Percent(sample.CpuUsagePercent),
            SystemMonitorContract.MemoryUsage =>
                OfTotal(sample.MemoryUsedBytes, sample.MemoryTotalBytes),
            SystemMonitorContract.GpuUsage => Percent(sample.GpuUsagePercent),
            SystemMonitorContract.GpuMemory =>
                OfTotal(sample.GpuMemoryUsedBytes, sample.GpuMemoryTotalBytes),
            SystemMonitorContract.DiskActivity => Percent(sample.DiskBusyPercent),
            SystemMonitorContract.DiskUsage =>
                OfTotal(sample.DiskUsedBytes, sample.DiskTotalBytes),
            SystemMonitorContract.NetworkUp =>
                Unbounded(sample.NetworkUpBytesPerSecond),
            SystemMonitorContract.NetworkDown =>
                Unbounded(sample.NetworkDownBytesPerSecond),
            // The clock is readable but has nothing to plot against: its turbo ceiling is not
            // exposed to user mode, and against the window's own peak it would be a flat line
            // near the top whatever the machine was doing. A temperature has no honest zero to
            // scale from either, and a fan's ceiling is the board's, not something we can read.
            _ => null,
        };
    }

    public static SystemMonitorMetricDto FormatMetric(
        SystemMetricSample sample,
        string metricId,
        SystemMonitorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(metricId);

        (string primary, string secondary, double? ratio, bool hasValue) =
            Describe(sample, metricId);

        string status = hasValue
            ? SystemMonitorMetricStatus.Ready
            : !sample.HasBaseline && RateMetrics.Contains(metricId)
                ? SystemMonitorMetricStatus.Pending
                : !sample.HasSensorSource && SensorSourceMetrics.Contains(metricId)
                    ? SystemMonitorMetricStatus.NeedsSensorSource
                    : SystemMonitorMetricStatus.Unavailable;

        return new SystemMonitorMetricDto
        {
            MetricId = metricId,
            IconId = ResolveIconId(metricId),
            Status = status,
            Detail = detail,
            PrimaryText = hasValue ? primary : Placeholder,
            SecondaryText = hasValue ? secondary : string.Empty,
            Ratio = hasValue ? ratio : null,
        };
    }

    /// <summary>
    /// Composes one taskbar segment. The text is final; the entry appends nothing to it.
    /// </summary>
    public static SystemMonitorSegmentDto FormatSegment(
        SystemMetricSample sample,
        string metricId,
        SystemMonitorDetail detail)
    {
        SystemMonitorMetricDto metric = FormatMetric(sample, metricId, detail);
        string tag = ResolveShortTag(metricId);

        string text = detail switch
        {
            SystemMonitorDetail.Compact => metric.PrimaryText,
            SystemMonitorDetail.Normal => Join(tag, metric.PrimaryText),
            _ => Join(tag, metric.PrimaryText, metric.SecondaryText),
        };

        if (text.Length > SystemMonitorContract.MaxSegmentTextLength)
        {
            text = text[..SystemMonitorContract.MaxSegmentTextLength];
        }

        return new SystemMonitorSegmentDto
        {
            MetricId = metricId,
            IconId = metric.IconId,
            Text = text,
        };
    }

    private static SystemMonitorMagnitude? Percent(double? percent) =>
        percent is { } value && double.IsFinite(value)
            ? new SystemMonitorMagnitude(Math.Clamp(value, 0d, 100d), 100d)
            : null;

    private static SystemMonitorMagnitude? OfTotal(double? used, double? total)
    {
        if (used is not { } value || !double.IsFinite(value) || value < 0d)
        {
            return null;
        }

        return total is { } ceiling && double.IsFinite(ceiling) && ceiling > 0d
            ? new SystemMonitorMagnitude(Math.Min(value, ceiling), ceiling)
            : new SystemMonitorMagnitude(value, null);
    }

    private static SystemMonitorMagnitude? Unbounded(double? value) =>
        value is { } rate && double.IsFinite(rate) && rate >= 0d
            ? new SystemMonitorMagnitude(rate, null)
            : null;

    private static string Join(params string[] parts) =>
        string.Join(' ', parts.Where(part => part.Length > 0));

    private static (string Primary, string Secondary, double? Ratio, bool HasValue) Describe(
        SystemMetricSample sample,
        string metricId) => metricId switch
    {
        SystemMonitorContract.CpuUsage =>
            FromPercent(sample.CpuUsagePercent),
        SystemMonitorContract.CpuClock =>
            FromClock(sample.CpuClockMhz),
        SystemMonitorContract.CpuTemperature =>
            FromTemperature(sample.CpuTemperatureCelsius),
        SystemMonitorContract.MemoryUsage =>
            FromUsedOfTotal(sample.MemoryUsedBytes, sample.MemoryTotalBytes),
        SystemMonitorContract.GpuUsage =>
            FromPercent(sample.GpuUsagePercent),
        SystemMonitorContract.GpuMemory =>
            FromUsedOfTotal(sample.GpuMemoryUsedBytes, sample.GpuMemoryTotalBytes),
        SystemMonitorContract.GpuTemperature =>
            FromTemperature(sample.GpuTemperatureCelsius),
        SystemMonitorContract.DiskActivity =>
            FromPercent(sample.DiskBusyPercent),
        SystemMonitorContract.DiskUsage =>
            FromUsedOfTotal(sample.DiskUsedBytes, sample.DiskTotalBytes),
        SystemMonitorContract.NetworkUp =>
            FromRate(sample.NetworkUpBytesPerSecond),
        SystemMonitorContract.NetworkDown =>
            FromRate(sample.NetworkDownBytesPerSecond),
        SystemMonitorContract.FanSpeed =>
            FromFan(sample.FanRpm),
        _ => (string.Empty, string.Empty, null, false),
    };

    private static (string, string, double?, bool) FromPercent(double? percent)
    {
        if (percent is not { } value || !double.IsFinite(value))
        {
            return (string.Empty, string.Empty, null, false);
        }

        double clamped = Math.Clamp(value, 0d, 100d);
        return (
            FormatNumber(clamped, 0) + "%",
            string.Empty,
            clamped / 100d,
            true);
    }

    private static (string, string, double?, bool) FromClock(double? megahertz)
    {
        if (megahertz is not { } value || !double.IsFinite(value) || value <= 0d)
        {
            return (string.Empty, string.Empty, null, false);
        }

        return value >= 1000d
            ? (FormatNumber(value / 1000d, 2) + " GHz", string.Empty, null, true)
            : (FormatNumber(value, 0) + " MHz", string.Empty, null, true);
    }

    private static (string, string, double?, bool) FromTemperature(double? celsius)
    {
        if (celsius is not { } value || !double.IsFinite(value))
        {
            return (string.Empty, string.Empty, null, false);
        }

        return (FormatNumber(value, 0) + " °C", string.Empty, null, true);
    }

    private static (string, string, double?, bool) FromFan(double? rpm)
    {
        if (rpm is not { } value || !double.IsFinite(value) || value < 0d)
        {
            return (string.Empty, string.Empty, null, false);
        }

        return (FormatNumber(value, 0) + " RPM", string.Empty, null, true);
    }

    private static (string, string, double?, bool) FromUsedOfTotal(
        double? usedBytes,
        double? totalBytes)
    {
        if (usedBytes is not { } used || !double.IsFinite(used) || used < 0d)
        {
            return (string.Empty, string.Empty, null, false);
        }

        if (totalBytes is not { } total || !double.IsFinite(total) || total <= 0d)
        {
            // Usage without a ceiling is still worth showing; it just has no meter.
            return (FormatBytes(used), string.Empty, null, true);
        }

        double ratio = Math.Clamp(used / total, 0d, 1d);
        return (
            FormatNumber(ratio * 100d, 0) + "%",
            FormatBytes(used) + " / " + FormatBytes(total),
            ratio,
            true);
    }

    private static (string, string, double?, bool) FromRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0d)
        {
            return (string.Empty, string.Empty, null, false);
        }

        // A rate has no ceiling, so it gets no meter - a bar drawn against an invented maximum
        // would imply a scale the reading does not have.
        return (FormatBytes(value) + "/s", string.Empty, null, true);
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(bytes, 0d);
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        // One decimal only while it still carries information: "1.2 MB" says something
        // "148.0 KB" does not.
        int decimals = unit == 0 || value >= 100d ? 0 : 1;
        return FormatNumber(value, decimals) + " " + units[unit];
    }

    private static string FormatNumber(double value, int decimals) =>
        Math.Round(value, decimals).ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
}
