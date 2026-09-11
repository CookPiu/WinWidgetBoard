using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Turns a window of raw samples into the 0..1 series the taskbar entry plots behind each
/// reading. Pure, like <see cref="SystemMonitorFormatter"/>, so the scaling rules are testable
/// without a machine that happens to have a GPU.
///
/// Two scales, because the metrics genuinely have two kinds:
///
/// - a reading with a ceiling (a percentage, or used-of-total) is drawn against that ceiling,
///   so a flat 20% CPU line stays low instead of filling the cell;
/// - a rate has no ceiling, so it is drawn against the window's own peak. That makes the shape
///   readable at any traffic level, at the cost of the height meaning nothing absolute - which
///   is the same trade every network graph makes, and why the number stays on top of it.
/// </summary>
public static class SystemMonitorHistory
{
    /// <summary>
    /// Below this, a peak-relative series is all noise: an idle link whose busiest sample is a
    /// few hundred bytes would otherwise be drawn as a full-height mountain range. Chosen at
    /// 4 KB/s, roughly the background chatter of an idle machine.
    /// </summary>
    private const double MinimumRatePeakBytesPerSecond = 4d * 1024d;

    /// <summary>
    /// The tail of <paramref name="samples"/> the surfaces actually plot. Callers that pair a
    /// series with anything else - the card's crosshair text, for one - take their window from
    /// here rather than repeating the arithmetic, so the two cannot come out different lengths
    /// and put a label on the wrong point.
    /// </summary>
    public static IReadOnlyList<SystemMetricSample> Window(
        IReadOnlyList<SystemMetricSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        int start = Math.Max(
            0,
            samples.Count - SystemMonitorContract.MaxHistorySamples);
        if (start == 0)
        {
            return samples;
        }

        var window = new SystemMetricSample[samples.Count - start];
        for (int index = 0; index < window.Length; index++)
        {
            window[index] = samples[start + index];
        }

        return window;
    }

    public static IReadOnlyList<double> Normalize(
        IReadOnlyList<SystemMetricSample> samples,
        string metricId)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(metricId);

        if (samples.Count == 0)
        {
            return Array.Empty<double>();
        }

        int start = Math.Max(
            0,
            samples.Count - SystemMonitorContract.MaxHistorySamples);
        var values = new List<double>(samples.Count - start);
        double windowPeak = 0d;
        double? fullScale = null;
        bool hasAnyReading = false;

        for (int index = start; index < samples.Count; index++)
        {
            SystemMonitorMagnitude? magnitude =
                SystemMonitorFormatter.TryGetMagnitude(samples[index], metricId);
            if (magnitude is not { } value)
            {
                // A gap in the middle of the window - the metric was pending for a tick - is
                // plotted as zero rather than skipped, so the series keeps its time spacing.
                values.Add(0d);
                continue;
            }

            hasAnyReading = true;
            values.Add(value.Value);
            windowPeak = Math.Max(windowPeak, value.Value);
            fullScale ??= value.FullScale;
        }

        // Nothing in the window could be read at all: no series rather than a flat zero line,
        // which would look like a real measurement of nothing happening. An idle network does
        // reach this point with a genuine run of zeros, and is drawn as a flat baseline.
        if (!hasAnyReading)
        {
            return Array.Empty<double>();
        }

        double divisor = fullScale is { } ceiling && ceiling > 0d
            ? ceiling
            : Math.Max(windowPeak, MinimumRatePeakBytesPerSecond);

        var normalized = new double[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            normalized[index] = Math.Clamp(values[index] / divisor, 0d, 1d);
        }

        return normalized;
    }
}
