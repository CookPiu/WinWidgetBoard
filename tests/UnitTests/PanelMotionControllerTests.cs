using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelMotionControllerTests
{
    private static readonly TimeSpan Frame =
        TimeSpan.FromSeconds(1.0 / 60);
    private static readonly TimeSpan FineStep =
        TimeSpan.FromMilliseconds(1);

    [TestMethod(DisplayName = "UT-PANEL-MOTION-001 [PNL-004] Panel animates opacity only and reverses from the displayed value")]
    public void PanelUsesOpacityOnlyAndReversesContinuously()
    {
        var controller = new PanelMotionController(false);
        controller.RequestOpen();
        for (int frame = 0; frame < 8; frame++)
            controller.Step(Frame);
        double before = controller.Value.Opacity;
        controller.RequestClose();
        double after = controller.Step(Frame).Opacity;
        Assert.IsTrue(Math.Abs(after - before) < 0.2);
        Settle(controller);
        Assert.AreEqual(PanelMotionState.Closed, controller.State);
        Assert.AreEqual(0, controller.Value.Opacity, 0.0001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-002 [NFR-A11Y-001] Reduced motion still provides a bounded fade")]
    public void ReducedMotionStillProvidesBoundedFade()
    {
        var controller = new PanelMotionController(true);
        controller.RequestOpen();
        Settle(controller);
        Assert.AreEqual(PanelMotionState.Open, controller.State);
        Assert.AreEqual(1, controller.Value.Opacity, 0.0001);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-003 [NFR-A11Y-001] Reduced motion fade completes inside its documented duration")]
    public void ReducedMotionFadeCompletesInsideDocumentedDuration()
    {
        // The spec commits to about a 180 ms material fade. Driving the ramp as
        // exp(-t/0.18) instead needed roughly 1 s to clear the settle threshold,
        // which made the accessible path slower than the full animation and held
        // the residency hide open with it.
        var opening = new PanelMotionController(true);
        opening.RequestOpen();
        TimeSpan openDuration = MeasureSettleDuration(opening, FineStep);

        var closing = new PanelMotionController(true);
        closing.RequestOpen();
        Settle(closing);
        closing.RequestClose();
        TimeSpan closeDuration = MeasureSettleDuration(closing, FineStep);

        Assert.AreEqual(PanelMotionState.Open, opening.State);
        Assert.AreEqual(PanelMotionState.Closed, closing.State);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(185), openDuration);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(185), closeDuration);

        // Guard the other direction too: an instant cut is not a fade.
        Assert.IsGreaterThan(TimeSpan.FromMilliseconds(90), openDuration);
        Assert.IsGreaterThan(TimeSpan.FromMilliseconds(90), closeDuration);
    }

    [TestMethod(DisplayName = "UT-PANEL-MOTION-004 [PNL-004] Dismissal does not take materially longer than reveal")]
    public void DismissalDoesNotTakeMateriallyLongerThanReveal()
    {
        var opening = new PanelMotionController(false);
        opening.RequestOpen();
        TimeSpan openDuration = MeasureSettleDuration(opening, FineStep);

        var closing = new PanelMotionController(false);
        closing.RequestOpen();
        Settle(closing);
        closing.RequestClose();
        TimeSpan closeDuration = MeasureSettleDuration(closing, FineStep);

        Assert.AreEqual(PanelMotionState.Open, opening.State);
        Assert.AreEqual(PanelMotionState.Closed, closing.State);

        // Closing at 14.0 against an opening 25.5 left the material on screen about
        // 1.8x as long on dismissal as on reveal.
        Assert.IsLessThan(
            openDuration * 1.25,
            closeDuration,
            $"close {closeDuration.TotalMilliseconds:F0} ms vs " +
            $"open {openDuration.TotalMilliseconds:F0} ms");
    }

    private static TimeSpan MeasureSettleDuration(
        PanelMotionController controller,
        TimeSpan step)
    {
        TimeSpan elapsed = TimeSpan.Zero;
        for (int tick = 0; tick < 5000 && controller.IsAnimating; tick++)
        {
            controller.Step(step);
            elapsed += step;
        }

        Assert.IsFalse(
            controller.IsAnimating,
            "Motion never settled.");
        return elapsed;
    }

    private static void Settle(PanelMotionController controller)
    {
        for (int frame = 0; frame < 300 && controller.IsAnimating; frame++)
            controller.Step(Frame);
    }
}
