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
        "UT-SYSMON-079 [MON-001] A reading waiting on the sensor source stays off the card")]
    public void NeedsSensorSourceRowIsDropped()
    {
        // A row held up by the optional sensor source would only ever repeat a setup
        // instruction, and an instruction is settings content: the settings page states the
        // HWiNFO/Core Temp requirement next to the metric lists instead. "Not available on
        // this PC" remains a card fact and keeps its row.
        SystemMonitorCardProjection projection = Project(
            Metric(
                SystemMonitorContract.CpuTemperature,
                "—",
                ratio: null,
                status: SystemMonitorMetricStatus.NeedsSensorSource));

        Assert.AreEqual(0, projection.Metrics.Count);
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

    [TestMethod(DisplayName =
        "UT-SYSMON-065 [MON-001] A tick leaves the paired rows alone; re-ordering rebuilds them")]
    public void PairsSurviveATickAndFollowAReorder()
    {
        var metrics = new ObservableCollection<SystemMonitorMetricViewModel>();
        var pairs = new ObservableCollection<SystemMonitorMetricPairViewModel>();
        SystemMonitorMetricListMerger.Merge(
            metrics,
            Project(
                Metric(SystemMonitorContract.CpuUsage, "10%", 0.1d),
                Metric(SystemMonitorContract.MemoryUsage, "50%", 0.5d),
                Metric(SystemMonitorContract.GpuUsage, "20%", 0.2d)).Metrics);
        SystemMonitorMetricPairListMerger.Merge(pairs, metrics);
        SystemMonitorMetricPairViewModel firstRow = pairs[0];

        // A tick changes only the numbers, and the view models are updated in place - so the
        // rows holding them must not be replaced either, or the card rebuilds its visuals
        // twice a second.
        SystemMonitorMetricListMerger.Merge(
            metrics,
            Project(
                Metric(SystemMonitorContract.CpuUsage, "11%", 0.11d),
                Metric(SystemMonitorContract.MemoryUsage, "52%", 0.52d),
                Metric(SystemMonitorContract.GpuUsage, "21%", 0.21d)).Metrics);
        SystemMonitorMetricPairListMerger.Merge(pairs, metrics);

        Assert.AreEqual(2, pairs.Count);
        Assert.AreSame(firstRow, pairs[0], "A value change must not replace a row.");
        Assert.IsNull(pairs[1].Right, "Three readings leave the last column short.");

        // Dropping one is a configuration change, and the pairing does have to follow it.
        SystemMonitorMetricListMerger.Merge(
            metrics,
            Project(
                Metric(SystemMonitorContract.MemoryUsage, "52%", 0.52d),
                Metric(SystemMonitorContract.CpuUsage, "11%", 0.11d)).Metrics);
        SystemMonitorMetricPairListMerger.Merge(pairs, metrics);

        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual(SystemMonitorContract.MemoryUsage, pairs[0].Left.MetricId);
        Assert.AreEqual(SystemMonitorContract.CpuUsage, pairs[0].Right?.MetricId);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-051 [MON-001] A reading's window becomes a curve ending at now")]
    public void HistoryBecomesCurve()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.1d, 0.5d, 0.41d])).Metrics.Single();

        Assert.AreEqual(3, row.CurvePoints.Count);
        Assert.AreEqual(0d, row.CurvePoints[0].Fraction);
        Assert.AreEqual(0.5d, row.CurvePoints[1].Fraction);
        Assert.AreEqual(1d, row.CurvePoints[^1].Fraction, "The newest sample is 'now'.");
        Assert.AreEqual(0.41d, row.CurvePoints[^1].Level);
        Assert.IsTrue(row.IsCurveVisible);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-052 [MON-001] One sample is not a curve, and the row keeps its meter")]
    public void SingleSampleIsNotACurve()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.41d])).Metrics.Single();

        Assert.AreEqual(0, row.CurvePoints.Count);
        Assert.IsFalse(row.IsCurveVisible);
        Assert.IsTrue(row.IsMeterVisible, "Before there is a window, the meter is the fallback.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-053 [MON-001] The curve replaces the meter rather than joining it")]
    public void CurveReplacesMeter()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.1d, 0.41d])).Metrics.Single();

        Assert.IsTrue(row.IsCurveVisible);
        Assert.IsFalse(
            row.IsMeterVisible,
            "The curve's last point is the number the meter would show.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-054 [MON-001] A rate gets a curve even though it gets no meter")]
    public void RateGetsCurveWithoutMeter()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.NetworkDown,
                "2.0 MB/s",
                ratio: null,
                history: [0.2d, 1d, 0.4d])).Metrics.Single();

        Assert.IsTrue(row.IsCurveVisible, "A rate is drawn against the window's own peak.");
        Assert.IsFalse(row.IsMeterVisible);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-055 [MON-001] Crosshair text names the value and how long ago it was read")]
    public void CurveTipNamesValueAndAge()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.1d, 0.5d, 0.41d],
                historyTexts: ["10%", "50%", "41%"])).Metrics.Single();

        // The resolver here returns the key rather than a template, so the argument lands
        // after it; what is being pinned is which wording each point gets and with what age.
        Assert.AreEqual("10% · [SysMonCurve.SecondsAgo] 4", row.CurvePoints[0].TipText);
        Assert.AreEqual("41% · [SysMonCurve.Now]", row.CurvePoints[^1].TipText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-056 [MON-001] Labels that do not line up with the series are dropped")]
    public void MisalignedHistoryTextsAreDropped()
    {
        SystemMonitorMetricRow row = Project(
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.1d, 0.5d, 0.41d],
                historyTexts: ["10%", "50%"])).Metrics.Single();

        Assert.IsTrue(row.IsCurveVisible, "The series itself is still drawable.");
        Assert.IsTrue(
            row.CurvePoints.All(point => point.TipText.Length == 0),
            "A label under the wrong point is worse than no crosshair text.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-057 [MON-001] A window with no stated cadence gets the value without an age")]
    public void NoCadenceMeansNoAge()
    {
        SystemMonitorMetricRow row = Project(
            0d,
            Metric(
                SystemMonitorContract.CpuUsage,
                "41%",
                ratio: 0.41d,
                history: [0.1d, 0.41d],
                historyTexts: ["10%", "41%"])).Metrics.Single();

        Assert.AreEqual("41%", row.CurvePoints[^1].TipText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-058 [MON-001] The headline is the first reading the card can show")]
    public void HeadlineIsTheFirstShowableReading()
    {
        SystemMonitorCardProjection projection = Project(
            Metric(
                SystemMonitorContract.CpuTemperature,
                "—",
                ratio: null,
                status: SystemMonitorMetricStatus.NeedsSensorSource),
            Metric(SystemMonitorContract.CpuUsage, "41%", ratio: 0.41d),
            Metric(SystemMonitorContract.MemoryUsage, "60%", ratio: 0.6d));

        Assert.AreEqual(SystemMonitorContract.CpuUsage, projection.Headline?.MetricId);
        Assert.AreEqual(
            SystemMonitorContract.MemoryUsage,
            projection.TrailingMetrics.Single().MetricId);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-059 [MON-001] With nothing to show there is no headline and no rows")]
    public void EmptyProjectionHasNoHeadline()
    {
        SystemMonitorCardProjection projection = Project();

        Assert.IsNull(projection.Headline);
        Assert.AreEqual(0, projection.TrailingMetrics.Count);
    }

    private static SystemMonitorCardProjection Project(params object[] metrics) =>
        Project(2d, metrics);

    private static SystemMonitorCardProjection Project(
        double sampleIntervalSeconds,
        params object[] metrics)
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
                new
                {
                    metrics,
                    sampledAtUtc = "2026-08-21T00:00:00Z",
                    sampleIntervalSeconds,
                },
                ContractJson.Options));
        return SystemMonitorCardProjection.FromSnapshot(snapshot, Resolver);
    }

    private static object Metric(
        string metricId,
        string primaryText,
        double? ratio,
        SystemMonitorDetail detail = SystemMonitorDetail.Detailed,
        string status = SystemMonitorMetricStatus.Ready,
        double[]? history = null,
        string[]? historyTexts = null) =>
        new
        {
            metricId,
            iconId = "cpu",
            status,
            detail = detail.ToString().ToLowerInvariant(),
            primaryText,
            secondaryText = "8.0 GB / 32 GB",
            ratio,
            history = history ?? Array.Empty<double>(),
            historyTexts = historyTexts ?? Array.Empty<string>(),
        };

    // Wrapping the key makes it obvious in an assertion whether the resolver was consulted.
    private static string Resolver(string key) => "[" + key + "]";
}
