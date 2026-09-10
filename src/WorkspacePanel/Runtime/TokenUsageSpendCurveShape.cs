namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// The spend curve's shape: the shared monotone spline (<see cref="MonotoneCurveShape"/>) plus
/// the one thing that is specific to spend - the day starts at the origin, midnight with
/// nothing spent, so the first hour rises out of the bottom-left corner rather than starting
/// wherever the first record happens to sit.
/// </summary>
public sealed class TokenUsageSpendCurveShape
{
    private readonly MonotoneCurveShape _shape;

    private TokenUsageSpendCurveShape(MonotoneCurveShape shape)
    {
        _shape = shape;
    }

    public IReadOnlyList<CurveKnot> Knots => _shape.Knots;

    /// <summary>True when there is something to draw: the origin plus at least one point.</summary>
    public bool HasSpans => _shape.HasSpans;

    /// <summary>
    /// Builds the shape from the broker's points, with the origin prepended. A point that does
    /// not advance past the previous one replaces it: the last point can share the current
    /// hour's closing position, and a zero-width span has no tangent.
    /// </summary>
    public static TokenUsageSpendCurveShape FromPoints(IReadOnlyList<TokenUsageSpendPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var knots = new List<CurveKnot>(points.Count + 1)
        {
            new(0d, 0d),
        };
        foreach (TokenUsageSpendPoint point in points)
        {
            knots.Add(new CurveKnot(point.Fraction, point.Level));
        }

        return new TokenUsageSpendCurveShape(MonotoneCurveShape.FromKnots(knots));
    }

    /// <summary>The curve's height at <paramref name="x"/>, clamped to the knots' range.</summary>
    public double LevelAt(double x) => _shape.LevelAt(x);

    /// <summary>The same curve as cubic Bézier spans.</summary>
    public IReadOnlyList<CurveSpan> Spans() => _shape.Spans();
}
