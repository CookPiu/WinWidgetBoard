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
        Assert.AreEqual(1, controller.Value.Scale, 0.001);
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
        Assert.AreEqual(1, controller.Value.Scale, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);
    }
}
