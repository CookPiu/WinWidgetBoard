namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// One hour of the trend, already worded. The curve control positions and draws; it never
/// formats a number or looks up a string, because the card is the only place that holds the
/// panel's resource loader and the reader's unit preference.
/// </summary>
public sealed record WeatherCurvePoint(
    double TemperatureCelsius,
    string TimeText,
    string TemperatureText,
    string ConditionText,
    string ConditionIconId,
    int? PrecipitationProbabilityPercent);

/// <summary>
/// The hourly trend as something a control can place: the shared monotone spline over the
/// hours, plus the temperature range it was normalised against.
///
/// The vertical axis spans the window's own minimum and maximum rather than a fixed range,
/// because a day that moves three degrees and a day that moves twenty are both worth seeing
/// the shape of. That is also why the axis is not zero-based the way the spend curve is:
/// zero degrees is not a floor, it is just another temperature.
///
/// WinUI-free so the normalisation is unit-tested; the control only scales the result.
/// </summary>
public sealed class WeatherHourlyCurveSeries
{
    /// <summary>
    /// A flat stretch still has to be drawn somewhere. Half height reads as "nothing is
    /// happening", which is the truth, where pinning it to the top or bottom would read as a
    /// record high or low.
    /// </summary>
    private const double FlatLevel = 0.5d;

    /// <summary>
    /// Below this the window is treated as flat. Normalising a range of a tenth of a degree
    /// turns rounding noise into a mountain range.
    /// </summary>
    private const double MinimumSpanCelsius = 0.5d;

    private WeatherHourlyCurveSeries(
        IReadOnlyList<WeatherCurvePoint> points,
        MonotoneCurveShape shape,
        double minimumCelsius,
        double maximumCelsius)
    {
        Points = points;
        Shape = shape;
        MinimumCelsius = minimumCelsius;
        MaximumCelsius = maximumCelsius;
    }

    public IReadOnlyList<WeatherCurvePoint> Points { get; }

    public MonotoneCurveShape Shape { get; }

    public double MinimumCelsius { get; }

    public double MaximumCelsius { get; }

    /// <summary>Two points is the least that has a shape.</summary>
    public bool HasCurve => Points.Count >= 2 && Shape.HasSpans;

    /// <summary>
    /// True when at least one hour carries a chance of precipitation above zero. The band is
    /// drawn only then: a row of empty slots under a dry forecast is a row of nothing.
    /// </summary>
    public bool HasPrecipitation =>
        Points.Any(point => point.PrecipitationProbabilityPercent is > 0);

    /// <summary>
    /// The horizontal position of an hour, 0 at the first and 1 at the last. Public because
    /// the control places the precipitation bars and the crosshair's hour boundaries from it.
    /// </summary>
    public double FractionAt(int index) =>
        Points.Count <= 1
            ? 0d
            : Math.Clamp((double)index / (Points.Count - 1), 0d, 1d);

    /// <summary>The hour a horizontal position falls in, or null when there are no points.</summary>
    public WeatherCurvePoint? PointAt(double fraction)
    {
        if (Points.Count == 0)
        {
            return null;
        }

        int index = (int)Math.Round(
            Math.Clamp(fraction, 0d, 1d) * (Points.Count - 1),
            MidpointRounding.AwayFromZero);
        return Points[Math.Clamp(index, 0, Points.Count - 1)];
    }

    public static WeatherHourlyCurveSeries FromPoints(IReadOnlyList<WeatherCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return new WeatherHourlyCurveSeries(
                points,
                MonotoneCurveShape.FromKnots([]),
                0d,
                0d);
        }

        double minimum = points.Min(point => point.TemperatureCelsius);
        double maximum = points.Max(point => point.TemperatureCelsius);
        double span = maximum - minimum;
        var knots = new List<CurveKnot>(points.Count);
        for (int index = 0; index < points.Count; index++)
        {
            double level = span < MinimumSpanCelsius
                ? FlatLevel
                : (points[index].TemperatureCelsius - minimum) / span;
            knots.Add(
                new CurveKnot(
                    points.Count <= 1 ? 0d : (double)index / (points.Count - 1),
                    level));
        }

        return new WeatherHourlyCurveSeries(
            points,
            MonotoneCurveShape.FromKnots(knots),
            minimum,
            maximum);
    }
}

/// <summary>
/// One forecast row: what it prints, plus where its span sits on the scale the visible days
/// share. The fractions are computed over the days actually shown, so a card showing three
/// days and one showing five each get a scale that fills their own bars.
/// </summary>
public sealed record WeatherDayRow(
    string DayText,
    string HighTemperatureText,
    string LowTemperatureText,
    string ConditionIconId,
    double Offset,
    double Length);

/// <summary>Places a set of forecast days on one shared temperature scale.</summary>
public static class WeatherDayScale
{
    /// <summary>
    /// Below this the whole set is treated as flat and every bar fills its track. Normalising
    /// a range of a fraction of a degree turns rounding into a week of dramatic swings.
    /// </summary>
    private const double MinimumSpanCelsius = 1d;

    public static IReadOnlyList<WeatherDayRow> Place(
        IReadOnlyList<WeatherDayProjection> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            return Array.Empty<WeatherDayRow>();
        }

        double minimum = days.Min(day => day.LowTemperatureCelsius);
        double maximum = days.Max(day => day.HighTemperatureCelsius);
        double span = maximum - minimum;
        var rows = new WeatherDayRow[days.Count];
        for (int index = 0; index < days.Count; index++)
        {
            WeatherDayProjection day = days[index];
            double offset = span < MinimumSpanCelsius
                ? 0d
                : Math.Clamp((day.LowTemperatureCelsius - minimum) / span, 0d, 1d);
            double length = span < MinimumSpanCelsius
                ? 1d
                : Math.Clamp(
                    (day.HighTemperatureCelsius - day.LowTemperatureCelsius) / span,
                    0d,
                    1d - offset);
            rows[index] = new WeatherDayRow(
                day.DayText,
                day.HighTemperatureText,
                day.LowTemperatureText,
                day.ConditionIconId,
                offset,
                length);
        }

        return rows;
    }
}
