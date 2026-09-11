namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// One reading's recent window as a polyline, and the lookups the crosshair needs over it.
///
/// A polyline rather than the spline the token card's spend curve uses, and deliberately: that
/// one plots a running total, where the value between two knots really did pass through the
/// curve. This one plots what the machine measured every two seconds, and a spline through
/// those samples would bow past them - drawing a peak the machine never reported, on a card
/// whose whole promise is that it does not invent readings. It is also what keeps sixty points
/// on up to eight rows affordable: one geometry segment per curve instead of fifty-nine.
///
/// WinUI-free on purpose, like <see cref="MonotoneCurveShape"/>: the placement arithmetic is
/// the part worth testing, and the control that scales it to pixels is not testable at all.
/// </summary>
public sealed class SystemMonitorCurveSeries
{
    private readonly IReadOnlyList<SystemMonitorCurvePoint> _points;

    private SystemMonitorCurveSeries(IReadOnlyList<SystemMonitorCurvePoint> points)
    {
        _points = points;
    }

    public IReadOnlyList<SystemMonitorCurvePoint> Points => _points;

    /// <summary>
    /// Null for anything that cannot be drawn as a line. One point is a dot: it says nothing
    /// about movement, and the row falls back to its meter instead.
    /// </summary>
    public static SystemMonitorCurveSeries? FromPoints(
        IReadOnlyList<SystemMonitorCurvePoint>? points) =>
        points is { Count: > 1 } ? new SystemMonitorCurveSeries(points) : null;

    /// <summary>
    /// The curve's height at a horizontal position, 0..1 on both axes. Linear between the two
    /// samples it falls between, which is what the line itself draws - the pip has to sit on
    /// the line, not near it.
    /// </summary>
    public double LevelAt(double fraction)
    {
        double x = Math.Clamp(fraction, 0d, 1d);
        for (int index = 1; index < _points.Count; index++)
        {
            SystemMonitorCurvePoint previous = _points[index - 1];
            SystemMonitorCurvePoint current = _points[index];
            if (x > current.Fraction)
            {
                continue;
            }

            double span = current.Fraction - previous.Fraction;
            if (span <= 0d)
            {
                return current.Level;
            }

            double weight = (x - previous.Fraction) / span;
            return previous.Level + ((current.Level - previous.Level) * weight);
        }

        return _points[^1].Level;
    }

    /// <summary>
    /// The sample the pointer is standing on - the nearest one, not the next one: each point
    /// is a measurement at an instant rather than a bucket covering the span before it, so
    /// the reading the pointer is closest to is the one it is asking about.
    /// </summary>
    public int IndexAt(double fraction)
    {
        double x = Math.Clamp(fraction, 0d, 1d);
        int nearest = 0;
        double nearestDistance = double.PositiveInfinity;
        for (int index = 0; index < _points.Count; index++)
        {
            double distance = Math.Abs(_points[index].Fraction - x);
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
