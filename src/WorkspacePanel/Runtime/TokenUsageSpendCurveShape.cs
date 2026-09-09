namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>A point of the curve on its 0..1 axes.</summary>
public readonly record struct TokenUsageCurveKnot(double X, double Y);

/// <summary>
/// One cubic Bézier span of the curve, in the same 0..1 space as the knots. The control scales
/// these to pixels and hands them to a <c>BezierSegment</c>; nothing here knows about WinUI.
/// </summary>
public readonly record struct TokenUsageCurveSpan(
    TokenUsageCurveKnot Start,
    TokenUsageCurveKnot Control1,
    TokenUsageCurveKnot Control2,
    TokenUsageCurveKnot End);

/// <summary>
/// The smooth shape drawn through the spend points, and the value of that shape at any
/// horizontal position.
///
/// A shape-preserving cubic Hermite spline (Fritsch–Carlson). The choice is not cosmetic: the
/// points are hourly totals, and an ordinary Catmull-Rom or smoothed Bézier swings below zero
/// after a busy hour and puts a bump into a flat stretch. These tangents keep every span inside
/// its two end values and make each local peak or trough sit exactly on its knot, which is what
/// lets the crosshair read a position off the curve without implying a figure the broker never
/// sent.
///
/// Pure and WinUI-free, so the shape is unit-tested and the control only scales it.
/// </summary>
public sealed class TokenUsageSpendCurveShape
{
    private readonly TokenUsageCurveKnot[] _knots;
    private readonly double[] _tangents;

    private TokenUsageSpendCurveShape(TokenUsageCurveKnot[] knots, double[] tangents)
    {
        _knots = knots;
        _tangents = tangents;
    }

    public IReadOnlyList<TokenUsageCurveKnot> Knots => _knots;

    /// <summary>True when there is something to draw: the origin plus at least one point.</summary>
    public bool HasSpans => _knots.Length >= 2;

    /// <summary>
    /// Builds the shape from the broker's points. The origin - midnight, nothing spent - is
    /// always the first knot, so the first hour rises out of the bottom-left corner. A point
    /// that does not advance past the previous one replaces it: the last point can share the
    /// current hour's closing position, and a zero-width span has no tangent.
    /// </summary>
    public static TokenUsageSpendCurveShape FromPoints(IReadOnlyList<TokenUsageSpendPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var knots = new List<TokenUsageCurveKnot>(points.Count + 1)
        {
            new(0d, 0d),
        };
        foreach (TokenUsageSpendPoint point in points)
        {
            var knot = new TokenUsageCurveKnot(
                Math.Clamp(point.Fraction, 0d, 1d),
                Math.Clamp(point.Level, 0d, 1d));
            if (knot.X <= knots[^1].X)
            {
                knots[^1] = knot;
            }
            else
            {
                knots.Add(knot);
            }
        }

        return new TokenUsageSpendCurveShape(knots.ToArray(), MonotoneTangents(knots));
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
    public IReadOnlyList<TokenUsageCurveSpan> Spans()
    {
        if (!HasSpans)
        {
            return Array.Empty<TokenUsageCurveSpan>();
        }

        var spans = new TokenUsageCurveSpan[_knots.Length - 1];
        for (int i = 0; i < spans.Length; i++)
        {
            TokenUsageCurveKnot start = _knots[i];
            TokenUsageCurveKnot end = _knots[i + 1];
            double third = (end.X - start.X) / 3d;
            spans[i] = new TokenUsageCurveSpan(
                start,
                new TokenUsageCurveKnot(start.X + third, start.Y + _tangents[i] * third),
                new TokenUsageCurveKnot(end.X - third, end.Y - _tangents[i + 1] * third),
                end);
        }

        return spans;
    }

    /// <summary>
    /// Fritsch–Carlson tangents: the secant average where neighbouring slopes agree, zero
    /// where they do not or where a span is flat, then pulled in wherever a tangent pair would
    /// let the span overshoot. The result never leaves the interval between its two ends.
    /// </summary>
    private static double[] MonotoneTangents(List<TokenUsageCurveKnot> knots)
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
