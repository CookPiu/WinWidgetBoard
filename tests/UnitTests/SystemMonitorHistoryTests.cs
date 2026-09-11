using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The taskbar sparkline is drawn from these numbers and nothing else - the entry has no idea
/// what a byte or a percent is - so the scaling rules are pinned here.
/// </summary>
[TestClass]
public sealed class SystemMonitorHistoryTests
{
    private static readonly double[] BoundedExpectation = [0d, 0.25d, 1d];
    private static readonly double[] GapExpectation = [0.5d, 0d, 0.5d];

    [TestMethod(DisplayName =
        "UT-SYSMON-047 [MON-002] A metric with a ceiling is scaled against that ceiling")]
    public void BoundedMetricScalesAgainstItsCeiling()
    {
        SystemMetricSample[] samples =
        [
            Sample(cpuUsagePercent: 0d),
            Sample(cpuUsagePercent: 25d),
            Sample(cpuUsagePercent: 100d),
        ];

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.CpuUsage);

        CollectionAssert.AreEqual(
            BoundedExpectation,
            history.ToArray(),
            "A quiet CPU must stay low rather than being stretched to fill the cell.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-048 [MON-002] A rate is scaled against the window's own peak")]
    public void RateScalesAgainstWindowPeak()
    {
        // Well above the idle floor, so the peak governs.
        SystemMetricSample[] samples =
        [
            Sample(networkDownBytesPerSecond: 0d),
            Sample(networkDownBytesPerSecond: 500d * 1024d),
            Sample(networkDownBytesPerSecond: 1000d * 1024d),
        ];

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.NetworkDown);

        Assert.AreEqual(0d, history[0], 0.0001d);
        Assert.AreEqual(0.5d, history[1], 0.0001d);
        Assert.AreEqual(1d, history[2], 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-049 [MON-002] An idle link is a flat baseline, not a mountain range")]
    public void IdleRateStaysFlat()
    {
        SystemMetricSample[] samples =
        [
            Sample(networkUpBytesPerSecond: 0d),
            Sample(networkUpBytesPerSecond: 120d),
            Sample(networkUpBytesPerSecond: 40d),
        ];

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.NetworkUp);

        Assert.AreEqual(3, history.Count);
        Assert.IsTrue(
            history.All(value => value < 0.1d),
            "Background chatter must not be amplified into a full-height graph.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-050 [MON-002] A reading this machine cannot take produces no series")]
    public void UnreadableMetricProducesNoSeries()
    {
        SystemMetricSample[] samples = [Sample(), Sample(), Sample()];

        Assert.AreEqual(
            0,
            SystemMonitorHistory.Normalize(
                samples,
                SystemMonitorContract.CpuTemperature).Count);

        // An idle network, by contrast, is a real run of zeros and must still be drawn.
        Assert.AreEqual(
            3,
            SystemMonitorHistory.Normalize(
                [
                    Sample(networkUpBytesPerSecond: 0d),
                    Sample(networkUpBytesPerSecond: 0d),
                    Sample(networkUpBytesPerSecond: 0d),
                ],
                SystemMonitorContract.NetworkUp).Count);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-051 [MON-002] A gap keeps its slot so the series keeps its time spacing")]
    public void GapKeepsItsSlot()
    {
        SystemMetricSample[] samples =
        [
            Sample(cpuUsagePercent: 50d),
            Sample(),
            Sample(cpuUsagePercent: 50d),
        ];

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.CpuUsage);

        CollectionAssert.AreEqual(GapExpectation, history.ToArray());
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-052 [MON-002] The series is capped at the contract's window")]
    public void SeriesIsCappedAtTheContractWindow()
    {
        SystemMetricSample[] samples = Enumerable
            .Range(0, SystemMonitorContract.MaxHistorySamples * 2)
            .Select(index => Sample(cpuUsagePercent: index % 101))
            .ToArray();

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.CpuUsage);

        Assert.AreEqual(SystemMonitorContract.MaxHistorySamples, history.Count);

        // The tail is what survives: the sparkline shows the recent past, not the oldest one.
        double expectedLast =
            samples[^1].CpuUsagePercent!.Value / 100d;
        Assert.AreEqual(expectedLast, history[^1], 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-053 [MON-002] A used-of-total series uses the total, not the window peak")]
    public void UsedOfTotalUsesTheTotal()
    {
        SystemMetricSample[] samples =
        [
            Sample(
                memoryUsedBytes: 4L * 1024 * 1024 * 1024,
                memoryTotalBytes: 32L * 1024 * 1024 * 1024),
            Sample(
                memoryUsedBytes: 8L * 1024 * 1024 * 1024,
                memoryTotalBytes: 32L * 1024 * 1024 * 1024),
        ];

        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.MemoryUsage);

        Assert.AreEqual(0.125d, history[0], 0.0001d);
        Assert.AreEqual(0.25d, history[1], 0.0001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-064 [MON-002] The plotted window and its labels come from one place")]
    public void WindowIsSharedWithWhateverLabelsIt()
    {
        var samples = new SystemMetricSample[SystemMonitorContract.MaxHistorySamples + 5];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = Sample(cpuUsagePercent: index);
        }

        IReadOnlyList<SystemMetricSample> window = SystemMonitorHistory.Window(samples);
        IReadOnlyList<double> history = SystemMonitorHistory.Normalize(
            samples,
            SystemMonitorContract.CpuUsage);
        IReadOnlyList<string> texts = SystemMonitorFormatter.FormatHistoryTexts(
            samples,
            SystemMonitorContract.CpuUsage,
            SystemMonitorDetail.Normal);

        Assert.AreEqual(SystemMonitorContract.MaxHistorySamples, window.Count);
        Assert.AreEqual(
            history.Count,
            texts.Count,
            "A label under the wrong sample is the failure this alignment prevents.");
        Assert.AreSame(samples[^1], window[^1], "The window is the tail, not the head.");
        Assert.AreEqual(
            "64%",
            texts[^1],
            "The newest label describes the newest sample, which is the 65th.");
    }

    private static SystemMetricSample Sample(
        double? cpuUsagePercent = null,
        double? memoryUsedBytes = null,
        double? memoryTotalBytes = null,
        double? networkUpBytesPerSecond = null,
        double? networkDownBytesPerSecond = null) =>
        new()
        {
            CpuUsagePercent = cpuUsagePercent,
            MemoryUsedBytes = memoryUsedBytes,
            MemoryTotalBytes = memoryTotalBytes,
            NetworkUpBytesPerSecond = networkUpBytesPerSecond,
            NetworkDownBytesPerSecond = networkDownBytesPerSecond,
            HasBaseline = true,
        };
}
