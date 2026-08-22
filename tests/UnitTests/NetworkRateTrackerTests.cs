using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NetworkRateTrackerTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-054 [MON-004] A newly visible interface cannot turn its lifetime counter into a rate")]
    public void JoiningInterfaceRestartsTheBaseline()
    {
        var tracker = new NetworkRateTracker();
        DateTimeOffset startedAt = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

        NetworkRateMeasurement first = tracker.Sample(
            [Counter("ethernet", received: 1_000L, sent: 500L)],
            startedAt);
        Assert.IsFalse(first.HasBaseline);

        NetworkRateMeasurement stable = tracker.Sample(
            [Counter("ethernet", received: 3_000L, sent: 1_000L)],
            startedAt.AddSeconds(2));
        Assert.IsTrue(stable.HasBaseline);
        Assert.AreEqual(250d, stable.UpBytesPerSecond!.Value, 0.001d);
        Assert.AreEqual(1_000d, stable.DownBytesPerSecond!.Value, 0.001d);

        NetworkRateMeasurement joined = tracker.Sample(
            [
                Counter("ethernet", received: 5_000L, sent: 1_500L),
                Counter("filter-layer", received: 50_000_000L, sent: 10_000_000L),
            ],
            startedAt.AddSeconds(4));
        Assert.IsFalse(joined.HasBaseline);
        Assert.IsNull(joined.UpBytesPerSecond);
        Assert.IsNull(joined.DownBytesPerSecond);

        NetworkRateMeasurement resumed = tracker.Sample(
            [
                Counter("ethernet", received: 7_000L, sent: 2_000L),
                Counter("filter-layer", received: 50_000_400L, sent: 10_000_400L),
            ],
            startedAt.AddSeconds(6));
        Assert.IsTrue(resumed.HasBaseline);
        Assert.AreEqual(450d, resumed.UpBytesPerSecond!.Value, 0.001d);
        Assert.AreEqual(1_200d, resumed.DownBytesPerSecond!.Value, 0.001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-055 [MON-004] An interface removal restarts the baseline instead of mixing totals")]
    public void RemovingInterfaceRestartsTheBaseline()
    {
        var tracker = new NetworkRateTracker();
        DateTimeOffset startedAt = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

        _ = tracker.Sample(
            [
                Counter("ethernet", received: 1_000L, sent: 500L),
                Counter("vpn", received: 5_000L, sent: 2_000L),
            ],
            startedAt);
        _ = tracker.Sample(
            [
                Counter("ethernet", received: 2_000L, sent: 700L),
                Counter("vpn", received: 5_100L, sent: 2_200L),
            ],
            startedAt.AddSeconds(2));

        NetworkRateMeasurement removed = tracker.Sample(
            [Counter("ethernet", received: 3_000L, sent: 900L)],
            startedAt.AddSeconds(4));
        Assert.IsFalse(removed.HasBaseline);
        Assert.IsNull(removed.UpBytesPerSecond);
        Assert.IsNull(removed.DownBytesPerSecond);

        NetworkRateMeasurement resumed = tracker.Sample(
            [Counter("ethernet", received: 3_500L, sent: 1_000L)],
            startedAt.AddSeconds(6));
        Assert.IsTrue(resumed.HasBaseline);
        Assert.AreEqual(50d, resumed.UpBytesPerSecond!.Value, 0.001d);
        Assert.AreEqual(250d, resumed.DownBytesPerSecond!.Value, 0.001d);
    }

    private static NetworkInterfaceCounterSnapshot Counter(string interfaceId, long received, long sent) =>
        new(interfaceId, received, sent);
}
