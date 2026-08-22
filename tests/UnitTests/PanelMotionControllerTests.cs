using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelMotionControllerTests
{
    [TestMethod(DisplayName = "UT-PANEL-MOTION-001 [PNL-003] Opening reaches the anchored open presentation")]
    public void OpeningReachesOpenPresentation()
    {
        var controller = new PanelMotionController(-14, 14, reducedMotion: false);

        controller.RequestOpen();
        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-002 [PNL-004] Closing reverses from the current presentation")]
    public void ClosingReversesFromCurrentPresentation()
    {
        var controller = new PanelMotionController(-14, 14, reducedMotion: false);

        controller.RequestOpen();
        for (int frame = 0; frame < 8; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        PanelMotionValue presentationBeforeClose = controller.Value;
        controller.RequestClose();
        PanelMotionValue firstClosingFrame = controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));

        Assert.AreEqual(PanelMotionState.Closing, controller.State);
        Assert.IsTrue(
            Math.Abs(firstClosingFrame.Opacity - presentationBeforeClose.Opacity) < 0.2);
        Assert.IsTrue(
            Math.Abs(firstClosingFrame.OffsetX - presentationBeforeClose.OffsetX) < 4);

        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.AreEqual(PanelMotionState.Closed, controller.State);
        Assert.AreEqual(0, controller.Value.Opacity, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-003 [NFR-A11Y-001] Reduced motion removes travel and scale")]
    public void ReducedMotionUsesOpacityOnly()
    {
        var controller = new PanelMotionController(-14, 14, reducedMotion: true);

        controller.RequestOpen();
        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-004 [PNL-003] Opening remains restrained without overshoot")]
    public void OpeningRemainsRestrainedWithoutOvershoot()
    {
        var controller = new PanelMotionController(-10, 10, reducedMotion: false);
        PanelMotionValue previous = controller.Value;

        Assert.AreEqual(0.95, previous.ScaleX, 0.001);
        Assert.AreEqual(0.88, previous.ScaleY, 0.001);
        controller.RequestOpen();
        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            PanelMotionValue current = controller.Step(
                TimeSpan.FromSeconds(1.0 / 60.0));

            Assert.IsTrue(current.Opacity >= previous.Opacity);
            Assert.IsTrue(current.Opacity <= 1);
            Assert.IsTrue(current.ScaleX >= previous.ScaleX);
            Assert.IsTrue(current.ScaleX <= 1);
            Assert.IsTrue(current.ScaleY >= previous.ScaleY);
            Assert.IsTrue(current.ScaleY <= 1);
            Assert.IsTrue(current.OffsetX >= previous.OffsetX);
            Assert.IsTrue(current.OffsetX <= 0);
            Assert.IsTrue(current.OffsetY <= previous.OffsetY);
            Assert.IsTrue(current.OffsetY >= 0);
            previous = current;
        }

        Assert.AreEqual(PanelMotionState.Open, controller.State);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-005 [PNL-003] Spring presentation is stable across frame cadence")]
    public void OpeningIsStableAcrossFrameCadence()
    {
        var thirtyFps = new PanelMotionController(-10, 10, reducedMotion: false);
        var oneTwentyFps = new PanelMotionController(-10, 10, reducedMotion: false);

        thirtyFps.RequestOpen();
        oneTwentyFps.RequestOpen();
        for (int frame = 0; frame < 9; frame++)
        {
            thirtyFps.Step(TimeSpan.FromSeconds(1.0 / 30.0));
        }

        for (int frame = 0; frame < 36; frame++)
        {
            oneTwentyFps.Step(TimeSpan.FromSeconds(1.0 / 120.0));
        }

        Assert.AreEqual(
            thirtyFps.Value.Opacity,
            oneTwentyFps.Value.Opacity,
            0.00001);
        Assert.AreEqual(
            thirtyFps.Value.ScaleX,
            oneTwentyFps.Value.ScaleX,
            0.00001);
        Assert.AreEqual(
            thirtyFps.Value.ScaleY,
            oneTwentyFps.Value.ScaleY,
            0.00001);
        Assert.AreEqual(
            thirtyFps.Value.OffsetX,
            oneTwentyFps.Value.OffsetX,
            0.00001);
        Assert.AreEqual(
            thirtyFps.Value.OffsetY,
            oneTwentyFps.Value.OffsetY,
            0.00001);
    }
}
