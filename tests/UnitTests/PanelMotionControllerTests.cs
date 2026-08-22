using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;
using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelMotionControllerTests
{
    [TestMethod(DisplayName = "UT-PANEL-MOTION-001 [PNL-003] Vertical sheet opening reaches the anchored open presentation")]
    public void VerticalSheetOpeningReachesOpenPresentation()
    {
        var controller = CreateController(PanelMotionAxis.Vertical);

        controller.RequestOpen();
        AdvanceToStable(controller);

        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-002 [PNL-004] Closing reverses from the current sheet presentation")]
    public void ClosingReversesFromCurrentPresentation()
    {
        var controller = CreateController(PanelMotionAxis.Vertical);

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
            Math.Abs(firstClosingFrame.ScaleY - presentationBeforeClose.ScaleY) < 0.04);
        Assert.IsTrue(
            Math.Abs(firstClosingFrame.OffsetY - presentationBeforeClose.OffsetY) < 6);

        AdvanceToStable(controller);

        Assert.AreEqual(PanelMotionState.Closed, controller.State);
        Assert.AreEqual(0, controller.Value.Opacity, 0.001);
        Assert.AreEqual(0.985, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(0.90, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(-8, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(36, controller.Value.OffsetY, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-003 [NFR-A11Y-001] Reduced motion removes sheet travel and scale")]
    public void ReducedMotionUsesOpacityOnly()
    {
        var controller = CreateController(
            PanelMotionAxis.Horizontal,
            reducedMotion: true);

        Assert.AreEqual(1, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);

        controller.RequestOpen();
        AdvanceToStable(controller);

        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleX, 0.001);
        Assert.AreEqual(1, controller.Value.ScaleY, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetX, 0.001);
        Assert.AreEqual(0, controller.Value.OffsetY, 0.001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-004 [PNL-003] Vertical sheet stays restrained without overshoot")]
    public void VerticalSheetRemainsRestrainedWithoutOvershoot()
    {
        var controller = CreateController(PanelMotionAxis.Vertical);
        PanelMotionValue previous = controller.Value;

        Assert.AreEqual(0.985, previous.ScaleX, 0.001);
        Assert.AreEqual(0.90, previous.ScaleY, 0.001);
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

    [TestMethod(DisplayName = "UT-PANEL-MOTION-005 [PNL-003] Horizontal sheet stays restrained without overshoot")]
    public void HorizontalSheetRemainsRestrainedWithoutOvershoot()
    {
        var controller = CreateController(PanelMotionAxis.Horizontal);
        PanelMotionValue previous = controller.Value;

        Assert.AreEqual(0.90, previous.ScaleX, 0.001);
        Assert.AreEqual(0.985, previous.ScaleY, 0.001);
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

    [TestMethod(DisplayName = "UT-PANEL-MOTION-006 [PNL-003] Entry edge selects the closest external axis")]
    public void EntryEdgeSelectsClosestExternalAxis()
    {
        PanelPlacement placement = new(
            new ScreenRect(100, 40, 1500, 1000),
            96,
            IsValid: true,
            Reason: string.Empty);
        PanelLaunchContext bottomEntry = new(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1920, 1032),
            new ScreenRect(900, 1032, 1020, 1080),
            96);
        PanelLaunchContext leftEntry = new(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1920, 1032),
            new ScreenRect(0, 400, 40, 500),
            96);
        PanelLaunchContext diagonalEntry = new(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1920, 1032),
            new ScreenRect(0, 0, 20, 20),
            96);
        PanelLaunchContext staleEntry = new(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1920, 1032),
            new ScreenRect(-200, 400, -100, 500),
            96);

        Assert.AreEqual(
            PanelMotionAxis.Vertical,
            PanelMotionAxisResolver.Resolve(bottomEntry, placement));
        Assert.AreEqual(
            PanelMotionAxis.Horizontal,
            PanelMotionAxisResolver.Resolve(leftEntry, placement));
        Assert.AreEqual(
            PanelMotionAxis.Vertical,
            PanelMotionAxisResolver.Resolve(diagonalEntry, placement));
        Assert.AreEqual(
            PanelMotionAxis.Vertical,
            PanelMotionAxisResolver.Resolve(staleEntry, placement));
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-007 [PNL-003] Sheet presentation is stable across frame cadence")]
    public void OpeningIsStableAcrossFrameCadence()
    {
        var thirtyFps = CreateController(PanelMotionAxis.Horizontal);
        var oneTwentyFps = CreateController(PanelMotionAxis.Horizontal);

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
            0.00002);
        Assert.AreEqual(
            thirtyFps.Value.ScaleX,
            oneTwentyFps.Value.ScaleX,
            0.00002);
        Assert.AreEqual(
            thirtyFps.Value.ScaleY,
            oneTwentyFps.Value.ScaleY,
            0.00002);
        Assert.AreEqual(
            thirtyFps.Value.OffsetX,
            oneTwentyFps.Value.OffsetX,
            0.00002);
        Assert.AreEqual(
            thirtyFps.Value.OffsetY,
            oneTwentyFps.Value.OffsetY,
            0.00002);
    }

    private static PanelMotionController CreateController(
        PanelMotionAxis primaryAxis,
        bool reducedMotion = false) =>
        primaryAxis == PanelMotionAxis.Horizontal
            ? new PanelMotionController(-36, 8, primaryAxis, reducedMotion)
            : new PanelMotionController(-8, 36, primaryAxis, reducedMotion);

    private static void AdvanceToStable(PanelMotionController controller)
    {
        for (int frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromSeconds(1.0 / 60.0));
        }
    }
}
