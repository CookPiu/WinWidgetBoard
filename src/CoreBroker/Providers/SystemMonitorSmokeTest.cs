using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Samples the machine twice and asserts the readings a normal, unelevated process must always
/// be able to produce. Counter availability varies by machine - a virtual machine has no GPU
/// counter set, a container has no physical disk - so only CPU and memory are treated as
/// mandatory; everything else is printed for review rather than asserted.
/// </summary>
internal static class SystemMonitorSmokeTest
{
    private static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync()
    {
        using var sampler = new SystemMetricSampler();

        SystemMetricSample first = sampler.Sample(DateTimeOffset.UtcNow);
        if (first.HasBaseline)
        {
            Console.Error.WriteLine(
                "CoreBroker sysmon smoke failed: the first sample claimed a baseline it " +
                "cannot have.");
            return 1;
        }

        await Task.Delay(SampleGap).ConfigureAwait(false);
        SystemMetricSample second = sampler.Sample(DateTimeOffset.UtcNow);
        if (!second.HasBaseline)
        {
            Console.Error.WriteLine(
                "CoreBroker sysmon smoke failed: the second sample has no baseline.");
            return 1;
        }

        // The ceilings come from the registry and from GlobalMemoryStatusEx rather than from a
        // counter, so print them: a metric that silently loses its total degrades from a
        // percentage to a bare number, which is easy to miss in the formatted output alone.
        Console.WriteLine(
            "totals: memory=" + Describe(second.MemoryTotalBytes) +
            " gpuMemory=" + Describe(second.GpuMemoryTotalBytes) +
            " disk=" + Describe(second.DiskTotalBytes));

        // Printed, never asserted: temperature and fan come from a monitoring program's shared
        // memory, and most machines are not running one. Absence is the expected state here,
        // not a failure.
        Console.WriteLine(
            "sensor sources: cpuTemperature=" + Present(second.HasCpuTemperatureSource) +
            " gpuTemperature=" + Present(second.HasGpuTemperatureSource) +
            " fan=" + Present(second.HasFanSource));

        foreach (string metricId in SystemMonitorContract.MetricIds)
        {
            SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
                second,
                metricId,
                SystemMonitorDetail.Detailed);
            SystemMonitorSegmentDto segment = SystemMonitorFormatter.FormatSegment(
                second,
                metricId,
                SystemMonitorDetail.Detailed);
            Console.WriteLine(
                $"{metricId,-18} {metric.Status,-12} " +
                $"primary={metric.PrimaryText,-12} secondary={metric.SecondaryText,-20} " +
                $"ratio={metric.Ratio?.ToString("F3", CultureInfo.InvariantCulture) ?? "-",-6} entry=\"{segment.Text}\"");
        }

        // CPU and memory come from GetSystemTimes and GlobalMemoryStatusEx, which are present on
        // every Windows install. If those are missing, the sampler is broken, not the machine.
        string[] mandatory =
        [
            SystemMonitorContract.CpuUsage,
            SystemMonitorContract.MemoryUsage,
        ];

        foreach (string metricId in mandatory)
        {
            SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
                second,
                metricId,
                SystemMonitorDetail.Normal);
            if (!string.Equals(
                    metric.Status,
                    SystemMonitorMetricStatus.Ready,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"CoreBroker sysmon smoke failed: '{metricId}' reported " +
                    $"'{metric.Status}' instead of a reading.");
                return 1;
            }
        }

        Console.WriteLine("SYSMON-SMOKE-PASS");
        return 0;
    }

    private static string Present(bool present) => present ? "present" : "absent";

    private static string Describe(double? bytes) =>
        bytes is { } value
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : "absent";
}
