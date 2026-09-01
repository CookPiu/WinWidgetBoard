using System.Collections.ObjectModel;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SystemMonitorCardProjectionTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-020 [MON-001] The card projection localizes names and keeps broker text")]
    public void ProjectionLocalizesNamesOnly()
    {
        SystemMonitorCardProjection projection = Project(
            Metric(SystemMonitorContract.CpuUsage, "41%", ratio: 0.41d));

        SystemMonitorMetricRow row = projection.Metrics.Single();
        Assert.AreEqual("[SysMonMetric.CpuUsage]", row.Name);
        Assert.AreEqual("41%", row.PrimaryText);
        Assert.IsTrue(projection.HasData);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-021 [MON-001] A metric this build cannot name is dropped, not shown unlabelled")]
    public void UnknownMetricIsDropped()
    {
        SystemMonitorCardProjection projection = Project(
            Metric("cpu.aura", "41%", ratio: 0.41d),
            Metric(SystemMonitorContract.MemoryUsage, "60%", ratio: 0.6d));

        Assert.AreEqual(1, projection.Metrics.Count);
        Assert.AreEqual(
            SystemMonitorContract.MemoryUsage,
            projection.Metrics[0].MetricId);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-022 [MON-001] The meter appears only with a ratio and above compact detail")]
    public void MeterVisibilityFollowsRatioAndDetail()
    {
        SystemMonitorMetricRow withRatio = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                detail: SystemMonitorDetail.Normal)).Metrics.Single();
        SystemMonitorMetricRow compact = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                detail: SystemMonitorDetail.Compact)).Metrics.Single();
        SystemMonitorMetricRow rate = Project(
            Metric(
                SystemMonitorContract.NetworkUp,
                "2.0 KB/s",
                ratio: null,
                detail: SystemMonitorDetail.Normal)).Metrics.Single();

        Assert.IsTrue(withRatio.IsMeterVisible);
        Assert.IsFalse(compact.IsMeterVisible, "Compact rows are a single line.");
        Assert.IsFalse(rate.IsMeterVisible, "A rate has no full scale to draw against.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-023 [MON-001] A metric with no reading shows its state instead of a number")]
    public void UnavailableMetricShowsState()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuTemperature,
                "—",
                ratio: null,
                status: SystemMonitorMetricStatus.Unavailable)).Metrics.Single();

        Assert.IsFalse(row.HasReading);
        Assert.IsTrue(row.IsMetricStatusTextVisible);
        Assert.AreEqual("[SysMonStatus.Unavailable]", row.MetricStatusText);
        Assert.IsFalse(row.IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-079 [MON-001] A reading waiting on the sensor source is worded differently")]
    public void NeedsSensorSourceIsItsOwnWording()
    {
        // "Not available on this PC" and "start HWiNFO" are different facts, and only one of
        // them is something the user can act on. Temperature has no user-mode API at all
        // (ADR-0029), so it is read from HWiNFO's shared memory when that is running.
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuTemperature,
                "—",
                ratio: null,
                status: SystemMonitorMetricStatus.NeedsSensorSource)).Metrics.Single();

        Assert.IsFalse(row.HasReading);
        Assert.IsTrue(row.IsMetricStatusTextVisible);
        Assert.AreEqual("[SysMonStatus.NeedsSensorSource]", row.MetricStatusText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-024 [MON-001] A payload with no metrics array projects empty rather than throwing")]
    public void PlaceholderPayloadProjectsEmpty()
    {
        var snapshot = new CardRuntimeSnapshot(
            BuiltInCardCatalog.SystemMonitorInstanceId,
            BuiltInCardCatalog.SystemMonitorCardTypeId,
            BuiltInCardRuntimeFactory.CurrentSchemaVersion,
            sequence: 1,
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Loading,
            JsonSerializer.SerializeToElement(new { placeholder = true }));

        SystemMonitorCardProjection projection =
            SystemMonitorCardProjection.FromSnapshot(snapshot, Resolver);

        Assert.AreEqual(0, projection.Metrics.Count);
        Assert.IsFalse(projection.HasData);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-025 [MON-001] Refreshing keeps the existing rows instead of rebuilding them")]
    public void MergeUpdatesRowsInPlace()
    {
        var target = new ObservableCollection<SystemMonitorMetricViewModel>();
        SystemMonitorMetricListMerger.Merge(
            target,
            Project(
                Metric(SystemMonitorContract.CpuUsage, "10%", 0.1d),
                Metric(SystemMonitorContract.MemoryUsage, "50%", 0.5d)).Metrics);

        SystemMonitorMetricViewModel first = target[0];
        SystemMonitorMetricViewModel second = target[1];

        SystemMonitorMetricListMerger.Merge(
            target,
            Project(
                Metric(SystemMonitorContract.CpuUsage, "72%", 0.72d),
                Metric(SystemMonitorContract.MemoryUsage, "51%", 0.51d)).Metrics);

        Assert.AreSame(first, target[0], "A value change must not replace the row.");
        Assert.AreSame(second, target[1]);
        Assert.AreEqual("72%", target[0].PrimaryText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-026 [MON-001] Reordering moves rows and dropping one removes it")]
    public void MergeHandlesMembershipChanges()
    {
        var target = new ObservableCollection<SystemMonitorMetricViewModel>();
        SystemMonitorMetricListMerger.Merge(
            target,
            Project(
                Metric(SystemMonitorContract.CpuUsage, "10%", 0.1d),
                Metric(SystemMonitorContract.MemoryUsage, "50%", 0.5d),
                Metric(SystemMonitorContract.GpuUsage, "20%", 0.2d)).Metrics);
        SystemMonitorMetricViewModel memory = target[1];

        SystemMonitorMetricListMerger.Merge(
            target,
            Project(
                Metric(SystemMonitorContract.MemoryUsage, "55%", 0.55d),
                Metric(SystemMonitorContract.CpuUsage, "11%", 0.11d)).Metrics);

        Assert.AreEqual(2, target.Count);
        Assert.AreSame(memory, target[0], "A move must keep the same row instance.");
        Assert.AreEqual(SystemMonitorContract.CpuUsage, target[1].MetricId);
    }

    private static SystemMonitorCardProjection Project(params object[] metrics)
    {
        var snapshot = new CardRuntimeSnapshot(
            BuiltInCardCatalog.SystemMonitorInstanceId,
            BuiltInCardCatalog.SystemMonitorCardTypeId,
            BuiltInCardRuntimeFactory.CurrentSchemaVersion,
            sequence: 1,
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Ready,
            JsonSerializer.SerializeToElement(
                new { metrics, sampledAtUtc = "2026-08-21T00:00:00Z" },
                ContractJson.Options));
        return SystemMonitorCardProjection.FromSnapshot(snapshot, Resolver);
    }

    private static object Metric(
        string metricId,
        string primaryText,
        double? ratio,
        SystemMonitorDetail detail = SystemMonitorDetail.Detailed,
        string status = SystemMonitorMetricStatus.Ready) =>
        new
        {
            metricId,
            iconId = "cpu",
            status,
            detail = detail.ToString().ToLowerInvariant(),
            primaryText,
            secondaryText = "8.0 GB / 32 GB",
            ratio,
        };

    // Wrapping the key makes it obvious in an assertion whether the resolver was consulted.
    private static string Resolver(string key) => "[" + key + "]";
}
