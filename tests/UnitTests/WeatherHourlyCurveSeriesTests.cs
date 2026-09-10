using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The hourly trend's normalisation. The control only scales what this produces, so anything
/// the curve can get wrong about which hour is where, or how tall a flat day looks, is
/// decidable here without a desktop.
/// </summary>
[TestClass]
public sealed class WeatherHourlyCurveSeriesTests
{
    [TestMethod(DisplayName =
        "UT-WEA-099 [WEA-001/CRD-001] The trend spans its own high and low, not a zero baseline")]
    public void LevelsSpanTheWindowsOwnRange()
    {
        WeatherHourlyCurveSeries series = Build(20d, 25d, 30d);

        Assert.IsTrue(series.HasCurve);
        Assert.AreEqual(20d, series.MinimumCelsius, 0.001);
        Assert.AreEqual(30d, series.MaximumCelsius, 0.001);
        // Zero degrees is not a floor, so the lowest hour sits on the bottom of the band and
        // the highest on the top whatever the absolute numbers are.
        Assert.AreEqual(0d, series.Shape.Knots[0].Y, 0.001);
        Assert.AreEqual(0.5d, series.Shape.Knots[1].Y, 0.001);
        Assert.AreEqual(1d, series.Shape.Knots[2].Y, 0.001);
        Assert.AreEqual(0d, series.FractionAt(0), 0.001);
        Assert.AreEqual(0.5d, series.FractionAt(1), 0.001);
        Assert.AreEqual(1d, series.FractionAt(2), 0.001);
    }

    [TestMethod(DisplayName =
        "UT-WEA-100 [WEA-001/CRD-001] A day that barely moves is drawn flat, not as a mountain range")]
    public void NearlyFlatWindowsStayFlat()
    {
        // A tenth of a degree of rounding noise normalised over its own range would fill the
        // whole band and read as a dramatic swing.
        WeatherHourlyCurveSeries series = Build(21.0d, 21.1d, 21.0d, 21.2d);

        foreach (CurveKnot knot in series.Shape.Knots)
        {
            Assert.AreEqual(0.5d, knot.Y, 0.001);
        }
    }

    [TestMethod(DisplayName =
        "UT-WEA-101 [WEA-001/CRD-001] The readout names a real hour, never an interpolated one")]
    public void PointAtSnapsToTheNearestHour()
    {
        WeatherHourlyCurveSeries series = Build(20d, 24d, 28d, 32d);

        // Between two hours the readout takes the nearer one: the broker sends hourly
        // readings, and a temperature halfway between two of them was never forecast.
        Assert.AreEqual("20.0", series.PointAt(0d)!.TemperatureText);
        Assert.AreEqual("24.0", series.PointAt(0.34d)!.TemperatureText);
        Assert.AreEqual("28.0", series.PointAt(0.66d)!.TemperatureText);
        Assert.AreEqual("32.0", series.PointAt(1d)!.TemperatureText);
        // Past either end it stays on the end hour rather than running off the series.
        Assert.AreEqual("20.0", series.PointAt(-4d)!.TemperatureText);
        Assert.AreEqual("32.0", series.PointAt(9d)!.TemperatureText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-102 [WEA-001/CRD-001] The rain band appears only where a forecast actually has one")]
    public void PrecipitationIsPresentOnlyWhenForecast()
    {
        Assert.IsFalse(Build(20d, 21d).HasPrecipitation);
        Assert.IsFalse(
            WeatherHourlyCurveSeries.FromPoints(
            [
                Point(20d, null),
                Point(21d, 0),
            ]).HasPrecipitation);
        // A single hour with a real chance is enough to earn the band.
        Assert.IsTrue(
            WeatherHourlyCurveSeries.FromPoints(
            [
                Point(20d, null),
                Point(21d, 35),
            ]).HasPrecipitation);
    }

    [TestMethod(DisplayName =
        "UT-WEA-103 [WEA-001/CRD-001] Too few hours is no curve rather than a broken one")]
    public void ShortSeriesHaveNoCurve()
    {
        Assert.IsFalse(WeatherHourlyCurveSeries.FromPoints([]).HasCurve);
        Assert.IsFalse(Build(21d).HasCurve);
        Assert.IsNull(WeatherHourlyCurveSeries.FromPoints([]).PointAt(0.5d));
        Assert.IsTrue(Build(21d, 22d).HasCurve);
    }

    private static WeatherHourlyCurveSeries Build(params double[] temperatures) =>
        WeatherHourlyCurveSeries.FromPoints(
            temperatures.Select(value => Point(value, null)).ToArray());

    private static WeatherCurvePoint Point(double celsius, int? precipitation) =>
        new(
            celsius,
            "12:00",
            celsius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            "晴",
            "clear-day",
            precipitation);
}
