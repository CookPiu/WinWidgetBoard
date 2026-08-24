using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Motion;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardFoldMotionControllerTests
{
    [TestMethod(DisplayName = "UT-CARD-FOLD-001 [PNL-003] Arbitrary card set opens in stable order")]
    public void ArbitraryCardSetOpensInStableOrder()
    {
        var controller = new CardFoldMotionController(false);
        string[] ids = ["a", "b", "c", "d", "e", "f"];
        controller.Configure(ids);
        controller.RequestOpen();
        for (int frame = 0; frame < 300 && controller.IsAnimating; frame++)
            controller.Step(TimeSpan.FromSeconds(1.0 / 60));
        CollectionAssert.AreEqual(ids, controller.Values.Select(v => v.InstanceId).ToArray());
        Assert.IsTrue(controller.Values.All(v => Math.Abs(v.Progress - 1) < 0.0001));
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-002 [CRD-003] Reorder preserves presentation by instance identity")]
    public void ReorderPreservesPresentationByInstanceIdentity()
    {
        var controller = new CardFoldMotionController(false);
        controller.Configure(["a", "b", "c"]);
        controller.RequestOpen();
        for (int frame = 0; frame < 7; frame++)
            controller.Step(TimeSpan.FromSeconds(1.0 / 60));
        var before = controller.Values.ToDictionary(v => v.InstanceId, v => v.Progress);
        controller.Configure(["c", "a", "b"]);
        foreach (CardFoldMotionValue value in controller.Values)
            Assert.AreEqual(before[value.InstanceId], value.Progress, 0.000001);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-003 [PNL-004] Close to open retarget keeps continuous progress")]
    public void CloseToOpenRetargetKeepsContinuousProgress()
    {
        var controller = new CardFoldMotionController(false);
        controller.Configure(["a", "b", "c"]);
        controller.RequestOpen();
        for (int frame = 0; frame < 10; frame++)
            controller.Step(TimeSpan.FromSeconds(1.0 / 60));
        controller.RequestClose();
        controller.Step(TimeSpan.FromSeconds(1.0 / 60));
        double before = controller.Values[1].Progress;
        controller.RequestOpen();
        double after = controller.Step(TimeSpan.FromSeconds(1.0 / 60))[1].Progress;
        Assert.IsTrue(Math.Abs(after - before) < 0.2);
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-004 [NFR-A11Y-001] Reduced motion keeps cards flat")]
    public void ReducedMotionKeepsCardsFlat()
    {
        var controller = new CardFoldMotionController(true);
        controller.Configure(["a", "b"]);
        controller.RequestOpen();
        controller.RequestClose();
        Assert.IsFalse(controller.IsAnimating);
        Assert.IsTrue(controller.Values.All(v => v.Progress == 1));
    }

    [TestMethod(DisplayName = "UT-CARD-FOLD-005 [CRD-003] Duplicate identity is rejected")]
    public void DuplicateIdentityIsRejected()
    {
        var controller = new CardFoldMotionController(false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            controller.Configure(["a", "a"]));
    }
}
