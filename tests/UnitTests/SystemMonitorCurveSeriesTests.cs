using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SystemMonitorCurveSeriesTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-060 [MON-001] Fewer than two samples make no series")]
    public void ShortWindowsMakeNoSeries()
    {
        Assert.IsNull(SystemMonitorCurveSeries.FromPoints(null));
        Assert.IsNull(SystemMonitorCurveSeries.FromPoints([]));
        Assert.IsNull(SystemMonitorCurveSeries.FromPoints([Point(0d, 0.5d)]));
        Assert.IsNotNull(
            SystemMonitorCurveSeries.FromPoints([Point(0d, 0.5d), Point(1d, 0.7d)]));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-061 [MON-001] The pip sits on the line between two samples, not near it")]
    public void LevelIsLinearBetweenSamples()
    {
        SystemMonitorCurveSeries series = Series(
            Point(0d, 0d),
            Point(0.5d, 1d),
            Point(1d, 0d));

        Assert.AreEqual(0d, series.LevelAt(0d), 1e-9);
        Assert.AreEqual(0.5d, series.LevelAt(0.25d), 1e-9);
        Assert.AreEqual(1d, series.LevelAt(0.5d), 1e-9);
        Assert.AreEqual(0.5d, series.LevelAt(0.75d), 1e-9);
        Assert.AreEqual(0d, series.LevelAt(1d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-062 [MON-001] A position outside the window clamps to its ends")]
    public void LevelClampsOutsideTheWindow()
    {
        SystemMonitorCurveSeries series = Series(Point(0d, 0.2d), Point(1d, 0.8d));

        Assert.AreEqual(0.2d, series.LevelAt(-1d), 1e-9);
        Assert.AreEqual(0.8d, series.LevelAt(2d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-063 [MON-001] The crosshair reads the sample it is nearest")]
    public void IndexPicksTheNearestSample()
    {
        SystemMonitorCurveSeries series = Series(
            Point(0d, 0d),
            Point(0.5d, 1d),
            Point(1d, 0d));

        Assert.AreEqual(0, series.IndexAt(0.1d));
        Assert.AreEqual(1, series.IndexAt(0.4d));
        Assert.AreEqual(1, series.IndexAt(0.6d));
        Assert.AreEqual(2, series.IndexAt(0.9d));
    }

    private static SystemMonitorCurveSeries Series(params SystemMonitorCurvePoint[] points)
    {
        SystemMonitorCurveSeries? series = SystemMonitorCurveSeries.FromPoints(points);
        Assert.IsNotNull(series);
        return series;
    }

    private static SystemMonitorCurvePoint Point(double fraction, double level) =>
        new() { Fraction = fraction, Level = level };
}
