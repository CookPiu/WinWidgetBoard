using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The shape the token card draws through the day's spend. What is pinned here is honesty
/// rather than looks: between two hours the curve may never leave the range of their two
/// values - no swing below a quiet hour, no bump in a flat stretch - and the value the
/// crosshair reads off it has to be the same curve that was drawn.
/// </summary>
[TestClass]
public sealed class TokenUsageSpendCurveShapeTests
{
    [TestMethod(DisplayName =
        "UT-TOKUSE-127 [USE-009] The curve starts at the origin and passes through every point")]
    public void CurvePassesThroughThePoints()
    {
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(0.25d, 0.1d), Point(0.5d, 0.6d), Point(1d, 1d)]);

        Assert.AreEqual(4, shape.Knots.Count);
        Assert.AreEqual(new CurveKnot(0d, 0d), shape.Knots[0]);
        Assert.AreEqual(0.1d, shape.LevelAt(0.25d), 1e-9);
        Assert.AreEqual(0.6d, shape.LevelAt(0.5d), 1e-9);
        Assert.AreEqual(1d, shape.LevelAt(1d), 1e-9);
        Assert.AreEqual(0d, shape.LevelAt(-1d), 1e-9);
        Assert.AreEqual(1d, shape.LevelAt(2d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-128 [USE-009] Between two hours the curve never leaves their two values")]
    public void CurveStaysBetweenNeighbouringPoints()
    {
        // A quiet morning, a burst, a flat stretch, then a drop to nothing: the shape a
        // smoothed curve most likes to overshoot on either side of the burst and undershoot
        // after the drop.
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(0.2d, 0d), Point(0.4d, 0.05d), Point(0.6d, 1d), Point(0.8d, 1d), Point(1d, 0d)]);

        IReadOnlyList<CurveKnot> knots = shape.Knots;
        for (int i = 0; i < knots.Count - 1; i++)
        {
            double low = Math.Min(knots[i].Y, knots[i + 1].Y);
            double high = Math.Max(knots[i].Y, knots[i + 1].Y);
            for (int step = 0; step <= 200; step++)
            {
                double x = knots[i].X + (knots[i + 1].X - knots[i].X) * step / 200d;
                double level = shape.LevelAt(x);
                Assert.IsTrue(
                    level >= low - 1e-9 && level <= high + 1e-9,
                    $"left [{low}, {high}] at x={x}: {level}");
            }
        }

        // The flat hour stays flat: no bump borrowed from the burst before it.
        Assert.AreEqual(1d, shape.LevelAt(0.7d), 1e-9);
        // The drop ends on the floor, not below it.
        Assert.AreEqual(0d, shape.LevelAt(1d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-129 [USE-009] The drawn spans and the evaluated curve are the same curve")]
    public void SpansMatchTheEvaluatedCurve()
    {
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(0.3d, 0.2d), Point(0.7d, 0.7d), Point(1d, 1d)]);

        IReadOnlyList<CurveSpan> spans = shape.Spans();
        Assert.AreEqual(3, spans.Count);
        foreach (CurveSpan span in spans)
        {
            for (int step = 0; step <= 20; step++)
            {
                double t = step / 20d;
                double u = 1d - t;
                double x = u * u * u * span.Start.X + 3d * u * u * t * span.Control1.X
                    + 3d * u * t * t * span.Control2.X + t * t * t * span.End.X;
                double y = u * u * u * span.Start.Y + 3d * u * u * t * span.Control1.Y
                    + 3d * u * t * t * span.Control2.Y + t * t * t * span.End.Y;
                Assert.AreEqual(shape.LevelAt(x), y, 1e-9, $"span from {span.Start.X} at t={t}");
            }
        }
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-130 [USE-009] A point that does not advance replaces the one before it")]
    public void RepeatedPositionCollapses()
    {
        // Just after midnight the only hour closes at the same position as "now".
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(1d, 0.4d), Point(1d, 1d)]);

        Assert.AreEqual(2, shape.Knots.Count);
        Assert.IsTrue(shape.HasSpans);
        Assert.AreEqual(1d, shape.LevelAt(1d), 1e-9);
        Assert.AreEqual(0, TokenUsageSpendCurveShape.FromPoints([]).Spans().Count);
    }

    private static TokenUsageSpendPoint Point(double fraction, double level) =>
        new() { Fraction = fraction, Level = level };
}
