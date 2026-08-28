using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SystemMonitorProviderTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-069 [MON-001/LCH-002] Taskbar monitoring samples once per second")]
    public void TaskbarMonitoringSamplesOncePerSecond()
    {
        using var provider = new SystemMonitorProvider(static () => []);
        ScheduledProviderDescriptor descriptor = provider.Descriptor;
        TimeSpan expected = TimeSpan.FromSeconds(1);

        Assert.AreEqual(expected, descriptor.MinimumInterval);
        Assert.AreEqual(expected, descriptor.VisibleInterval);
        Assert.AreEqual(expected, descriptor.HiddenInterval);
        Assert.AreEqual(expected, descriptor.ManualRefreshMinimumInterval);
    }
}
