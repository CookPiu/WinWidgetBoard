using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The shape the token card draws through the day's spend. What is pinned here is honesty
/// rather than looks: the curve is a running total, so between two hours it may never dip
/// below the earlier one or rise above the later one, and the value the crosshair reads off it
/// has to be the same curve that was drawn.
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
        Assert.AreEqual(new TokenUsageCurveKnot(0d, 0d), shape.Knots[0]);
        Assert.AreEqual(0.1d, shape.LevelAt(0.25d), 1e-9);
        Assert.AreEqual(0.6d, shape.LevelAt(0.5d), 1e-9);
        Assert.AreEqual(1d, shape.LevelAt(1d), 1e-9);
        Assert.AreEqual(0d, shape.LevelAt(-1d), 1e-9);
        Assert.AreEqual(1d, shape.LevelAt(2d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-128 [USE-009] Between two hours the curve never leaves their two values")]
    public void CurveIsMonotoneBetweenPoints()
    {
        // A quiet morning, a burst, then a flat stretch: the shape a smoothed curve most
        // likes to overshoot on either side of the burst.
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(0.2d, 0d), Point(0.4d, 0.05d), Point(0.6d, 0.9d), Point(0.8d, 0.9d), Point(1d, 1d)]);

        double previous = 0d;
        for (int step = 0; step <= 1000; step++)
        {
            double x = step / 1000d;
            double level = shape.LevelAt(x);
            Assert.IsTrue(level >= previous - 1e-9, $"dipped at x={x}: {level} < {previous}");
            Assert.IsTrue(level >= 0d && level <= 1d, $"left the range at x={x}: {level}");
            previous = level;
        }

        // The flat hour stays flat: no bump borrowed from the burst before it.
        Assert.AreEqual(0.9d, shape.LevelAt(0.7d), 1e-9);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-129 [USE-009] The drawn spans and the evaluated curve are the same curve")]
    public void SpansMatchTheEvaluatedCurve()
    {
        TokenUsageSpendCurveShape shape = TokenUsageSpendCurveShape.FromPoints(
            [Point(0.3d, 0.2d), Point(0.7d, 0.7d), Point(1d, 1d)]);

        IReadOnlyList<TokenUsageCurveSpan> spans = shape.Spans();
        Assert.AreEqual(3, spans.Count);
        foreach (TokenUsageCurveSpan span in spans)
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
