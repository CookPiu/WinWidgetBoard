using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardFoldPathResolverTests
{
    [TestMethod]
    public void ClosedCornerResolvesToLauncherAnchor()
    {
        var anchor = new MotionPoint(24, 700);
        var bounds = new MotionRect(40, 80, 300, 500);
        CardFoldPath path = CardFoldPathResolver.Resolve(anchor, bounds, 0, 3);
        Assert.AreEqual(anchor.X, bounds.X + path.StartX, 0.0001);
        Assert.AreEqual(anchor.Y, bounds.Bottom + path.StartY, 0.0001);
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
        Assert.AreEqual(0, value.RotationX, 0.0001);
        Assert.AreEqual(0, value.RotationY, 0.0001);
        Assert.AreEqual(0, value.RotationZ, 0.0001);
    }

    [TestMethod]
    public void OrderedCardsReceiveDistinctFoldAxes()
    {
        CardFoldPath[] paths = Enumerable.Range(0, 6)
            .Select(index => CardFoldPathResolver.Resolve(
                new MotionPoint(0, 700),
                new MotionRect(index * 10, index * 20, 200, 200),
                index,
                6))
            .ToArray();
        Assert.AreEqual(6, paths.Select(p => (p.ControlX, p.ControlY, p.RotationY))
            .Distinct().Count());
    }
}
