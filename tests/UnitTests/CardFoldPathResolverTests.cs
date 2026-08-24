using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardFoldPathResolverTests
{
    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-001 [PNL-003] Closed hinge travels toward the launcher anchor within budget")]
    public void ClosedHingeTravelsTowardLauncherAnchorWithinBudget()
    {
        var anchor = new MotionPoint(24, 700);
        var bounds = new MotionRect(40, 80, 300, 500);
        CardFoldPath path = CardFoldPathResolver.Resolve(anchor, bounds, 0, 3);

        // The card keeps the entry's bearing but must not fly out of the panel: the
        // content ScrollViewer clips it, so a full flight to the anchor spends most
        // of the animation invisible and reads as a jump back on the last frame.
        double reachX = anchor.X - (bounds.X + path.PivotX);
        double reachY = anchor.Y - (bounds.Y + path.PivotY);
        double reach = Math.Sqrt(reachX * reachX + reachY * reachY);
        double travel = Math.Sqrt(
            path.StartX * path.StartX + path.StartY * path.StartY);

        Assert.IsGreaterThan(CardFoldPathResolver.MaximumTravel, reach);
        Assert.AreEqual(CardFoldPathResolver.MaximumTravel, travel, 0.0001);
        Assert.AreEqual(reachX / reach, path.StartX / travel, 0.0001);
        Assert.AreEqual(reachY / reach, path.StartY / travel, 0.0001);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-002 [PNL-003] Open presentation is exact identity")]
    public void OpenPresentationIsExactIdentity()
    {
        CardFoldPath path = CardFoldPathResolver.Resolve(
            new MotionPoint(0, 700),
            new MotionRect(10, 20, 200, 300),
            1,
            3);
        CardFoldPresentation value = path.Evaluate(1);
        Assert.AreEqual(0, value.OffsetX, 0.0001);
        Assert.AreEqual(0, value.OffsetY, 0.0001);
        Assert.AreEqual(0, value.OffsetZ, 0.0001);
        Assert.AreEqual(1, value.Scale, 0.0001);
        Assert.AreEqual(0, value.FoldAngle, 0.0001);
        Assert.AreEqual(0, value.RotationZ, 0.0001);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-003 [PNL-003] Card fold axes radiate from the launcher anchor")]
    public void CardFoldAxesRadiateFromLauncherAnchor()
    {
        var anchor = new MotionPoint(20, 720);
        var bounds = new MotionRect(240, 80, 300, 260);
        CardFoldPath path = CardFoldPathResolver.Resolve(anchor, bounds, 2, 6);
        double deltaX = bounds.X + bounds.Width / 2 - anchor.X;
        double deltaY = bounds.Y + bounds.Height / 2 - anchor.Y;
        double length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        Assert.AreEqual(deltaX / length, path.AxisX, 0.0001);
        Assert.AreEqual(deltaY / length, path.AxisY, 0.0001);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-004 [PNL-003] Facing hinge lies on the card boundary toward the launcher")]
    public void FacingHingeLiesOnCardBoundaryTowardLauncher()
    {
        var anchor = new MotionPoint(20, 720);
        var bounds = new MotionRect(240, 80, 300, 260);
        CardFoldPath path = CardFoldPathResolver.Resolve(anchor, bounds, 2, 6);
        bool liesOnBoundary =
            Math.Abs(path.PivotX) < 0.0001 ||
            Math.Abs(path.PivotX - bounds.Width) < 0.0001 ||
            Math.Abs(path.PivotY) < 0.0001 ||
            Math.Abs(path.PivotY - bounds.Height) < 0.0001;
        Assert.IsTrue(liesOnBoundary);
        Assert.IsGreaterThanOrEqualTo(0, path.PivotX);
        Assert.IsLessThanOrEqualTo(bounds.Width, path.PivotX);
        Assert.IsGreaterThanOrEqualTo(0, path.PivotY);
        Assert.IsLessThanOrEqualTo(bounds.Height, path.PivotY);

        double fromCenterX = path.PivotX - bounds.Width / 2;
        double fromCenterY = path.PivotY - bounds.Height / 2;
        double cross = fromCenterX * -path.AxisY -
            fromCenterY * -path.AxisX;
        double dot = fromCenterX * -path.AxisX +
            fromCenterY * -path.AxisY;
        Assert.AreEqual(0, cross, 0.0001);
        Assert.IsGreaterThan(0, dot);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-005 [PNL-003] Ordered cards receive distinct radial axes")]
    public void OrderedCardsReceiveDistinctRadialAxes()
    {
        var anchor = new MotionPoint(0, 700);
        CardFoldPath[] paths = Enumerable.Range(0, 6)
            .Select(index => CardFoldPathResolver.Resolve(
                anchor,
                new MotionRect(
                    40 + index % 3 * 220,
                    60 + index / 3 * 240,
                    200,
                    200),
                index,
                6))
            .ToArray();
        Assert.AreEqual(
            paths.Length,
            paths.Select(path => (
                    Math.Round(path.AxisX, 6),
                    Math.Round(path.AxisY, 6)))
                .Distinct()
                .Count());
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-007 [PNL-003] Fold scale collapses to the uniform scale when the fold ends")]
    public void FoldScaleCollapsesToUniformScaleWhenFoldEnds()
    {
        // Weighting the split by the absolute axis components summed to 1.414 on a
        // diagonal axis, so every card stayed 41% oversized for the whole animation
        // and snapped back on the final frame. The pair must land on the uniform
        // scale for every axis once the fold angle reaches zero.
        foreach (int index in new[] { 0, 1, 2, 3, 4, 5 })
        {
            CardFoldPath path = CardFoldPathResolver.Resolve(
                new MotionPoint(20, 900),
                new MotionRect(40 + index % 2 * 380, 60 + index / 2 * 240, 350, 220),
                index,
                6);
            CardFoldPresentation landed = path.Evaluate(1);
            (double x, double y) = CardFoldPathResolver.ResolveFoldScale(landed);

            Assert.AreEqual(1, landed.Scale, 0.0001);
            Assert.AreEqual(1, x, 0.0001, $"card {index} horizontal scale");
            Assert.AreEqual(1, y, 0.0001, $"card {index} vertical scale");
        }
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-008 [PNL-003] Fold scale never exceeds the uniform scale")]
    public void FoldScaleNeverExceedsUniformScale()
    {
        CardFoldPath path = CardFoldPathResolver.Resolve(
            new MotionPoint(20, 900),
            new MotionRect(240, 80, 350, 220),
            1,
            4);

        for (int step = 0; step <= 20; step++)
        {
            CardFoldPresentation presentation = path.Evaluate(step / 20.0);
            (double x, double y) = CardFoldPathResolver.ResolveFoldScale(presentation);

            Assert.IsLessThan(presentation.Scale + 0.0001, x);
            Assert.IsLessThan(presentation.Scale + 0.0001, y);
            Assert.IsGreaterThan(0, x);
            Assert.IsGreaterThan(0, y);
        }
    }
    [TestMethod(DisplayName = "UT-CARD-FOLD-PATH-006 [PNL-003] Fold depth recedes at the hinge and eases forward mid flight")]
    public void FoldDepthRecedesAtHingeAndEasesForwardMidFlight()
    {
        // Depth is only visible because the overlay carries a perspective camera.
        // Without one, Composition projects orthographically and both OffsetZ and
        // Lift are inert -- this pins them as load-bearing so they cannot quietly
        // become dead values again.
        CardFoldPath path = CardFoldPathResolver.Resolve(
            new MotionPoint(20, 720),
            new MotionRect(240, 80, 300, 260),
            0,
            3);

        Assert.IsLessThan(-100, path.Evaluate(0).OffsetZ);
        Assert.AreEqual(0, path.Evaluate(1).OffsetZ, 0.0001);

        // Lift pulls the middle of the flight closer than a straight recession.
        double linearRecession = path.Evaluate(0).OffsetZ / 2;
        Assert.IsGreaterThan(linearRecession, path.Evaluate(0.5).OffsetZ);
        Assert.IsLessThan(0, path.Evaluate(0.5).OffsetZ);
    }
}
