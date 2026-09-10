namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>A point of a curve on its 0..1 axes.</summary>
public readonly record struct CurveKnot(double X, double Y);

/// <summary>
/// One cubic Bézier span of a curve, in the same 0..1 space as the knots. A control scales
/// these to pixels and hands them to a <c>BezierSegment</c>; nothing here knows about WinUI.
/// </summary>
public readonly record struct CurveSpan(
    CurveKnot Start,
    CurveKnot Control1,
    CurveKnot Control2,
    CurveKnot End);

/// <summary>
/// A shape-preserving cubic Hermite spline (Fritsch–Carlson) through a series of points, and
/// the value of that shape at any horizontal position.
///
/// The choice is not cosmetic. An ordinary Catmull-Rom or naively smoothed Bézier overshoots:
/// it swings below the lowest point after a steep span and puts a bump into a flat stretch.
/// These tangents keep every span inside its two end values and make each local peak or trough
/// sit exactly on its knot, which is what lets a crosshair read a position off the curve
/// without implying a figure the broker never sent - a temperature that never occurred, or a
/// spend total that never happened.
///
/// Pure and WinUI-free, so the shape is unit-tested and controls only scale it. Two cards draw
/// curves from it: the token card's cumulative spend and the weather card's hourly trend.
/// </summary>
public sealed class MonotoneCurveShape
{
    private readonly CurveKnot[] _knots;
    private readonly double[] _tangents;

    private MonotoneCurveShape(CurveKnot[] knots, double[] tangents)
    {
        _knots = knots;
        _tangents = tangents;
    }

    public IReadOnlyList<CurveKnot> Knots => _knots;

    /// <summary>True when there is something to draw: at least two distinct points.</summary>
    public bool HasSpans => _knots.Length >= 2;

    /// <summary>
    /// Builds the shape from knots already in 0..1 space. A knot that does not advance past
    /// the previous one replaces it: two points may share a position, and a zero-width span
    /// has no tangent.
    /// </summary>
    public static MonotoneCurveShape FromKnots(IEnumerable<CurveKnot> knots)
    {
        ArgumentNullException.ThrowIfNull(knots);

        var ordered = new List<CurveKnot>();
        foreach (CurveKnot knot in knots)
        {
            var clamped = new CurveKnot(
                Math.Clamp(knot.X, 0d, 1d),
                Math.Clamp(knot.Y, 0d, 1d));
            if (ordered.Count > 0 && clamped.X <= ordered[^1].X)
            {
                ordered[^1] = clamped;
            }
            else
            {
                ordered.Add(clamped);
            }
        }

        return new MonotoneCurveShape(ordered.ToArray(), MonotoneTangents(ordered));
    }

    /// <summary>The curve's height at <paramref name="x"/>, clamped to the knots' range.</summary>
    public double LevelAt(double x)
    {
        if (_knots.Length == 0)
        {
            return 0d;
        }

        if (_knots.Length == 1 || x <= _knots[0].X)
        {
            return _knots[0].Y;
        }

        if (x >= _knots[^1].X)
        {
            return _knots[^1].Y;
        }

        int i = 0;
        while (i < _knots.Length - 2 && x > _knots[i + 1].X)
        {
            i++;
        }

        double h = _knots[i + 1].X - _knots[i].X;
        double t = (x - _knots[i].X) / h;
        double t2 = t * t;
        double t3 = t2 * t;
        double h00 = 2d * t3 - 3d * t2 + 1d;
        double h10 = t3 - 2d * t2 + t;
        double h01 = -2d * t3 + 3d * t2;
        double h11 = t3 - t2;
        return h00 * _knots[i].Y
            + h10 * h * _tangents[i]
            + h01 * _knots[i + 1].Y
            + h11 * h * _tangents[i + 1];
    }

    /// <summary>
    /// The same curve as cubic Bézier spans. A Hermite span with tangents m0, m1 over width h
    /// is exactly the Bézier whose inner control points sit a third of the way along each
    /// tangent, so the drawn shape and <see cref="LevelAt"/> agree to the pixel.
    /// </summary>
    public IReadOnlyList<CurveSpan> Spans()
    {
        if (!HasSpans)
        {
            return Array.Empty<CurveSpan>();
        }

        var spans = new CurveSpan[_knots.Length - 1];
        for (int i = 0; i < spans.Length; i++)
        {
            CurveKnot start = _knots[i];
            CurveKnot end = _knots[i + 1];
            double third = (end.X - start.X) / 3d;
            spans[i] = new CurveSpan(
                start,
                new CurveKnot(start.X + third, start.Y + _tangents[i] * third),
                new CurveKnot(end.X - third, end.Y - _tangents[i + 1] * third),
                end);
        }

        return spans;
    }

    /// <summary>
    /// Fritsch–Carlson tangents: the secant average where neighbouring slopes agree, zero
    /// where they do not or where a span is flat, then pulled in wherever a tangent pair would
    /// let the span overshoot. The result never leaves the interval between its two ends.
    /// </summary>
    private static double[] MonotoneTangents(List<CurveKnot> knots)
    {
        int n = knots.Count;
        var tangents = new double[n];
        if (n < 2)
        {
            return tangents;
        }

        var deltas = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            deltas[i] = (knots[i + 1].Y - knots[i].Y) / (knots[i + 1].X - knots[i].X);
        }

        tangents[0] = deltas[0];
        tangents[n - 1] = deltas[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            tangents[i] = deltas[i - 1] * deltas[i] <= 0d
                ? 0d
                : (deltas[i - 1] + deltas[i]) / 2d;
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (deltas[i] == 0d)
            {
                tangents[i] = 0d;
                tangents[i + 1] = 0d;
                continue;
            }

            double a = tangents[i] / deltas[i];
            double b = tangents[i + 1] / deltas[i];
            double magnitude = a * a + b * b;
            if (magnitude > 9d)
            {
                double tau = 3d / Math.Sqrt(magnitude);
                tangents[i] = tau * a * deltas[i];
                tangents[i + 1] = tau * b * deltas[i];
            }
        }

        return tangents;
    }
}
