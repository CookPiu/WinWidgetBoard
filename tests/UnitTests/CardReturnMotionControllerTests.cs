using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Interaction;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardReturnMotionControllerTests
{
    [TestMethod(DisplayName = "UT-CARD-MOTION-001 [LYT-004] Cancel return reaches the drag start offset")]
    public void ReturnReachesTarget()
    {
        var controller = new CardReturnMotionController(reducedMotion: false);

        controller.Start(new DragOffset(42, -18), DragOffset.Zero);
        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.AreEqual(CardReturnMotionState.Idle, controller.State);
        Assert.AreEqual(DragOffset.Zero, controller.Value);
    }

    [TestMethod(DisplayName = "UT-CARD-MOTION-002 [LYT-004] Return can be interrupted from the current presentation")]
    public void ReturnCanBeInterrupted()
    {
        var controller = new CardReturnMotionController(reducedMotion: false);

        controller.Start(new DragOffset(40, 12), DragOffset.Zero);
        for (int frame = 0; frame < 8; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }

        DragOffset presentationBeforeInterrupt = controller.Value;
        controller.StopAt(presentationBeforeInterrupt);
        controller.Start(presentationBeforeInterrupt, new DragOffset(18, -6));
        DragOffset firstFrame = controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));

        Assert.IsTrue(Math.Abs(firstFrame.X - presentationBeforeInterrupt.X) < 4);
        Assert.IsTrue(Math.Abs(firstFrame.Y - presentationBeforeInterrupt.Y) < 4);
        Assert.IsTrue(controller.IsAnimating);
    }

    [TestMethod(DisplayName = "UT-CARD-MOTION-003 [NFR-A11Y-003] Reduced motion snaps return without travel")]
    public void ReducedMotionSnapsToTarget()
    {
        var controller = new CardReturnMotionController(reducedMotion: true);

        controller.Start(new DragOffset(42, -18), DragOffset.Zero);

        Assert.AreEqual(CardReturnMotionState.Idle, controller.State);
        Assert.AreEqual(DragOffset.Zero, controller.Value);
    }
}
