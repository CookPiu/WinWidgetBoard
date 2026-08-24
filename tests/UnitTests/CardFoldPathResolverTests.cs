using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardFoldPathResolverTests
{
    [TestMethod]
    public void ClosedHingeResolvesToLauncherAnchor()
    {
        var anchor = new MotionPoint(24, 700);
        var bounds = new MotionRect(40, 80, 300, 500);
        CardFoldPath path = CardFoldPathResolver.Resolve(anchor, bounds, 0, 3);
        Assert.AreEqual(
            anchor.X,
            bounds.X + path.PivotX + path.StartX,
            0.0001);
        Assert.AreEqual(
            anchor.Y,
            bounds.Y + path.PivotY + path.StartY,
            0.0001);
    }

    [TestMethod]
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

    [TestMethod]
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

    [TestMethod]
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

    [TestMethod]
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
}
