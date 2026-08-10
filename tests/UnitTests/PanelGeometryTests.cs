using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelGeometryTests
{
    [TestMethod(DisplayName = "UT-PANEL-GEO-001 [PNL-002/003] Panel geometry stays within the triggering work area")]
    public void PanelGeometryContractSmokePasses()
    {
        Assert.IsTrue(
            PanelGeometry.RunContractSmokeTest(out string failure),
            failure);
    }

    [TestMethod(DisplayName = "UT-PANEL-GEO-002 [PNL-002] Invalid launch geometry fails closed")]
    public void InvalidContextIsUnavailable()
    {
        var context = new PanelLaunchContext(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 0, 0),
            new ScreenRect(8, 976, 44, 1012),
            96);

        PanelPlacement placement = PanelGeometry.Calculate(context);

        Assert.IsFalse(placement.IsValid);
        StringAssert.Contains(placement.Reason, "invalid");
    }

    [TestMethod(DisplayName = "UT-PANEL-CONTEXT-001 [PNL-003] Complete launch context parses")]
    public void CompleteLaunchContextParses()
    {
        string[] arguments =
        [
            "--monitor-rect=-1920,0,0,1080",
            "--work-area=-1920,0,0,1032",
            "--launcher-rect=-1912,976,-1876,1012",
            "--dpi=144",
        ];

        bool parsed = PanelLaunchContext.TryParse(
            arguments,
            out PanelLaunchContext? context,
            out string? error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(context);
        Assert.AreEqual(144u, context!.Dpi);
        Assert.AreEqual(-1920, context.WorkAreaRect.Left);
    }
}
