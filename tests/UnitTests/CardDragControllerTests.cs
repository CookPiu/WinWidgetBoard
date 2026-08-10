using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Interaction;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardDragControllerTests
{
    [TestMethod(DisplayName = "UT-CARD-DRAG-001 [LYT-004] Movement below threshold remains a click")]
    public void MovementBelowThresholdRemainsPressed()
    {
        var controller = new CardDragController();

        controller.Press(new DragPoint(100, 80));
        CardDragUpdate move = controller.Move(new DragPoint(106, 86));

        Assert.AreEqual(CardDragState.Pressed, move.State);
        Assert.AreEqual(DragOffset.Zero, move.Offset);

        CardDragUpdate release = controller.Release(new DragPoint(106, 86));

        Assert.IsTrue(release.Clicked);
        Assert.IsFalse(release.Completed);
        Assert.AreEqual(CardDragState.Idle, release.State);
    }

    [TestMethod(DisplayName = "UT-CARD-DRAG-002 [LYT-004] Drag preserves the pointer delta")]
    public void DragPreservesGrabOffset()
    {
        var controller = new CardDragController();

        controller.Press(new DragPoint(100, 80));
        CardDragUpdate start = controller.Move(new DragPoint(111, 86));
        CardDragUpdate move = controller.Move(new DragPoint(90, 70));

        Assert.IsTrue(start.Started);
        Assert.AreEqual(CardDragState.Dragging, move.State);
        Assert.AreEqual(-10, move.Offset.X, 0.001);
        Assert.AreEqual(-10, move.Offset.Y, 0.001);

        CardDragUpdate release = controller.Release(new DragPoint(90, 70));

        Assert.IsTrue(release.Completed);
        Assert.IsFalse(release.Clicked);
        Assert.AreEqual(CardDragState.Idle, release.State);
    }

    [TestMethod(DisplayName = "UT-CARD-DRAG-003 [LYT-004] Escape or capture loss restores the start position")]
    public void CancelRestoresStartOffset()
    {
        var controller = new CardDragController();

        controller.Press(new DragPoint(40, 40));
        controller.Move(new DragPoint(60, 52));
        CardDragUpdate cancel = controller.Cancel();

        Assert.IsTrue(cancel.Canceled);
        Assert.AreEqual(DragOffset.Zero, cancel.Offset);
        Assert.AreEqual(CardDragState.Idle, cancel.State);
    }

    [TestMethod(DisplayName = "UT-CARD-DRAG-004 [LYT-004] Interrupted return preserves the current visual offset")]
    public void SetOffsetBecomesTheNextDragStartOffset()
    {
        var controller = new CardDragController();
        var currentOffset = new DragOffset(18, -6);

        controller.SetOffset(currentOffset);
        controller.Press(new DragPoint(100, 80));
        CardDragUpdate start = controller.Move(new DragPoint(111, 86));

        Assert.IsTrue(start.Started);
        Assert.AreEqual(currentOffset.X + 11, start.Offset.X, 0.001);
        Assert.AreEqual(currentOffset.Y + 6, start.Offset.Y, 0.001);
    }
}
