using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The broker owns every number the two surfaces display, so these are the tests that keep a
/// unit, a rounding rule or a placeholder from drifting.
/// </summary>
[TestClass]
public sealed class SystemMonitorFormatterTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-001 [MON-001] A percentage reading carries a meter fraction")]
    public void PercentMetricCarriesRatio()
    {
        SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
            Sample(cpuUsagePercent: 42.4d),
            SystemMonitorContract.CpuUsage,
            SystemMonitorDetail.Normal);

        Assert.AreEqual(SystemMonitorMetricStatus.Ready, metric.Status);
        Assert.AreEqual("42%", metric.PrimaryText);
        Assert.AreEqual(0.424d, metric.Ratio!.Value, 0.001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-002 [MON-001] A used-of-total reading shows a percentage and both sizes")]
    public void UsedOfTotalCarriesBothSides()
    {
        SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
            Sample(
                memoryUsedBytes: 8L * 1024 * 1024 * 1024,
                memoryTotalBytes: 32L * 1024 * 1024 * 1024),
            SystemMonitorContract.MemoryUsage,
            SystemMonitorDetail.Detailed);

        Assert.AreEqual("25%", metric.PrimaryText);
        Assert.AreEqual("8.0 GB / 32.0 GB", metric.SecondaryText);
        Assert.AreEqual(0.25d, metric.Ratio!.Value, 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-003 [MON-001] A rate has no meter, because it has no ceiling")]
    public void RateHasNoRatio()
    {
        SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
            Sample(networkDownBytesPerSecond: 1536d * 1024d),
            SystemMonitorContract.NetworkDown,
            SystemMonitorDetail.Normal);

        Assert.AreEqual("1.5 MB/s", metric.PrimaryText);
        Assert.IsNull(
            metric.Ratio,
            "A bar drawn against an invented maximum would imply a scale the reading lacks.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-004 [MON-001] A missing rate is pending before a baseline and unavailable after")]
    public void MissingRateDistinguishesPendingFromUnavailable()
    {
        SystemMonitorMetricDto pending = SystemMonitorFormatter.FormatMetric(
            Sample(hasBaseline: false),
            SystemMonitorContract.CpuUsage,
            SystemMonitorDetail.Normal);
        SystemMonitorMetricDto unavailable = SystemMonitorFormatter.FormatMetric(
            Sample(hasBaseline: true),
            SystemMonitorContract.CpuUsage,
            SystemMonitorDetail.Normal);

        Assert.AreEqual(SystemMonitorMetricStatus.Pending, pending.Status);
        Assert.AreEqual(SystemMonitorMetricStatus.Unavailable, unavailable.Status);
        Assert.AreEqual(SystemMonitorFormatter.Placeholder, pending.PrimaryText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-005 [MON-001] A sensor reading is never pending, whichever way it is missing")]
    public void SensorMetricsAreNeverPending()
    {
        foreach (string metricId in new[]
        {
            SystemMonitorContract.CpuTemperature,
            SystemMonitorContract.GpuTemperature,
            SystemMonitorContract.FanSpeed,
        })
        {
            // Pending means "wait one tick for a baseline", which only the metrics measured as
            // a difference have. These three are published whole by the sensor source or not at
            // all, so waiting is never the answer - which of the two unreadable states they get
            // is UT-SYSMON-078.
            foreach (bool hasSensorSource in new[] { false, true })
            {
                SystemMonitorMetricDto metric = SystemMonitorFormatter.FormatMetric(
                    Sample(hasBaseline: false, hasSensorSource: hasSensorSource),
                    metricId,
                    SystemMonitorDetail.Normal);
                Assert.AreNotEqual(
                    SystemMonitorMetricStatus.Pending,
                    metric.Status,
                    $"'{metricId}' has no baseline to wait for.");
            }
        }
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-006 [MON-002] Entry detail decides how much of a segment is composed")]
    public void SegmentTextFollowsDetail()
    {
        SystemMetricSample sample = Sample(
            memoryUsedBytes: 8L * 1024 * 1024 * 1024,
            memoryTotalBytes: 32L * 1024 * 1024 * 1024);

        Assert.AreEqual(
            "25%",
            Segment(sample, SystemMonitorDetail.Compact).Text);
        Assert.AreEqual(
            "MEM 25%",
            Segment(sample, SystemMonitorDetail.Normal).Text);
        Assert.AreEqual(
            "MEM 25% 8.0 GB / 32.0 GB",
            Segment(sample, SystemMonitorDetail.Detailed).Text);

        static SystemMonitorSegmentDto Segment(
            SystemMetricSample sample,
            SystemMonitorDetail detail) =>
            SystemMonitorFormatter.FormatSegment(
                sample,
                SystemMonitorContract.MemoryUsage,
                detail);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-007 [MON-002] A network segment relies on its glyph and adds no tag")]
    public void NetworkSegmentsCarryNoTag()
    {
        SystemMonitorSegmentDto segment = SystemMonitorFormatter.FormatSegment(
            Sample(networkUpBytesPerSecond: 2048d),
            SystemMonitorContract.NetworkUp,
            SystemMonitorDetail.Normal);

        Assert.AreEqual("net-up", segment.IconId);
        Assert.AreEqual(
            "2.0 KB/s",
            segment.Text,
            "The arrow already says which direction this is.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-008 [MON-002] Segment text is bounded so one metric cannot eat the strip")]
    public void SegmentTextIsBounded()
    {
        SystemMonitorSegmentDto segment = SystemMonitorFormatter.FormatSegment(
            Sample(
                memoryUsedBytes: 123456789012345d,
                memoryTotalBytes: 223456789012345d),
            SystemMonitorContract.MemoryUsage,
            SystemMonitorDetail.Detailed);

        Assert.IsTrue(
            segment.Text.Length <= SystemMonitorContract.MaxSegmentTextLength,
            $"Segment text was {segment.Text.Length} characters.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-009 [MON-001] Every metric maps to a glyph the entry can draw")]
    public void EveryMetricHasAGlyph()
    {
        foreach (string metricId in SystemMonitorContract.MetricIds)
        {
            Assert.AreNotEqual(
                "unknown",
                SystemMonitorFormatter.ResolveIconId(metricId),
                $"'{metricId}' has no glyph.");
        }
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-010 [MON-001] The clock reads in GHz above a gigahertz and carries no meter")]
    public void ClockScalesItsUnitAndHasNoRatio()
    {
        SystemMonitorMetricDto gigahertz = SystemMonitorFormatter.FormatMetric(
            Sample(cpuClockMhz: 4392d),
            SystemMonitorContract.CpuClock,
            SystemMonitorDetail.Detailed);
        SystemMonitorMetricDto megahertz = SystemMonitorFormatter.FormatMetric(
            Sample(cpuClockMhz: 780d),
            SystemMonitorContract.CpuClock,
            SystemMonitorDetail.Detailed);

        Assert.AreEqual(SystemMonitorMetricStatus.Ready, gigahertz.Status);
        Assert.AreEqual("4.39 GHz", gigahertz.PrimaryText);
        Assert.AreEqual("780 MHz", megahertz.PrimaryText);
        Assert.IsNull(
            gigahertz.Ratio,
            "The turbo ceiling is not readable from user mode, so there is no scale to draw.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-080 [MON-002] The clock's taskbar segment carries no CPU tag")]
    public void ClockSegmentCarriesNoTag()
    {
        // The clock shares the chip glyph with the usage row and its unit already names it;
        // a second "CPU" told the reader nothing and cost the width that pushed "GHz" into
        // the ellipsis.
        SystemMonitorSegmentDto segment = SystemMonitorFormatter.FormatSegment(
            Sample(cpuClockMhz: 4392d),
            SystemMonitorContract.CpuClock,
            SystemMonitorDetail.Normal);

        Assert.AreEqual("4.39 GHz", segment.Text);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-011 [MON-001] The clock is pending before a baseline, not unavailable")]
    public void MissingClockIsPendingBeforeABaseline()
    {
        SystemMonitorMetricDto pending = SystemMonitorFormatter.FormatMetric(
            Sample(hasBaseline: false),
            SystemMonitorContract.CpuClock,
            SystemMonitorDetail.Normal);
        SystemMonitorMetricDto unavailable = SystemMonitorFormatter.FormatMetric(
            Sample(hasBaseline: true),
            SystemMonitorContract.CpuClock,
            SystemMonitorDetail.Normal);

        Assert.AreEqual(
            SystemMonitorMetricStatus.Pending,
            pending.Status,
            "The counters need two collections before the first ratio exists.");
        Assert.AreEqual(SystemMonitorMetricStatus.Unavailable, unavailable.Status);
    }

    private static SystemMetricSample Sample(
        double? cpuUsagePercent = null,
        double? cpuClockMhz = null,
        double? memoryUsedBytes = null,
        double? memoryTotalBytes = null,
        double? networkUpBytesPerSecond = null,
        double? networkDownBytesPerSecond = null,
        bool hasBaseline = true,
        bool hasSensorSource = false) =>
        new()
        {
            CpuUsagePercent = cpuUsagePercent,
            CpuClockMhz = cpuClockMhz,
            MemoryUsedBytes = memoryUsedBytes,
            MemoryTotalBytes = memoryTotalBytes,
            NetworkUpBytesPerSecond = networkUpBytesPerSecond,
            NetworkDownBytesPerSecond = networkDownBytesPerSecond,
            HasBaseline = hasBaseline,
            HasCpuTemperatureSource = hasSensorSource,
            HasGpuTemperatureSource = hasSensorSource,
            HasFanSource = hasSensorSource,
        };
}
