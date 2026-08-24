using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelMotionControllerTests
{
    [TestMethod]
    public void PanelUsesOpacityOnlyAndReversesContinuously()
    {
        var controller = new PanelMotionController(false);
        controller.RequestOpen();
        for (int frame = 0; frame < 8; frame++)
            controller.Step(TimeSpan.FromSeconds(1.0 / 60));
        double before = controller.Value.Opacity;
        controller.RequestClose();
        double after = controller.Step(TimeSpan.FromSeconds(1.0 / 60)).Opacity;
        Assert.IsTrue(Math.Abs(after - before) < 0.2);
        Settle(controller);
        Assert.AreEqual(PanelMotionState.Closed, controller.State);
        Assert.AreEqual(0, controller.Value.Opacity, 0.0001);
    }

    [TestMethod]
    public void ReducedMotionStillProvidesBoundedFade()
    {
        var controller = new PanelMotionController(true);
        controller.RequestOpen();
        Settle(controller);
        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.0001);
    }

    private static void Settle(PanelMotionController controller)
    {
        for (int frame = 0; frame < 300 && controller.IsAnimating; frame++)
            controller.Step(TimeSpan.FromSeconds(1.0 / 60));
    }
}
