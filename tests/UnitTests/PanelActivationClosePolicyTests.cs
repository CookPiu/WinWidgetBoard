using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PanelActivationClosePolicyTests
{
    [TestMethod(DisplayName = "UT-PANEL-ACT-001 [PNL-004] Production deactivation closes an activated panel")]
    public void ProductionDeactivationClosesActivatedPanel()
    {
        Assert.IsTrue(PanelActivationClosePolicy.ShouldRequestClose(
            keepOpenForAcceptance: false,
            hasBeenActivated: true,
            modalScopeDepth: 0,
            isDeactivated: true));
    }

    [TestMethod(DisplayName = "UT-PANEL-ACT-002 [PNL-004] Modal and pre-activation deactivation do not close")]
    public void ModalAndPreActivationDeactivationDoNotClose()
    {
        Assert.IsFalse(PanelActivationClosePolicy.ShouldRequestClose(
            keepOpenForAcceptance: false,
            hasBeenActivated: false,
            modalScopeDepth: 0,
            isDeactivated: true));
        Assert.IsFalse(PanelActivationClosePolicy.ShouldRequestClose(
            keepOpenForAcceptance: false,
            hasBeenActivated: true,
            modalScopeDepth: 1,
            isDeactivated: true));
    }

    [TestMethod(DisplayName = "UT-PANEL-ACT-003 [BLD-001] Acceptance panels remain available to UI Automation")]
    public void AcceptancePanelRemainsAvailableToUiAutomation()
    {
        Assert.IsFalse(PanelActivationClosePolicy.ShouldRequestClose(
            keepOpenForAcceptance: true,
            hasBeenActivated: true,
            modalScopeDepth: 0,
            isDeactivated: true));
    }

    [TestMethod(DisplayName = "UT-PANEL-ACT-004 [PNL-004] Invalid modal depth fails closed")]
    public void InvalidModalDepthFailsClosed()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PanelActivationClosePolicy.ShouldRequestClose(
                keepOpenForAcceptance: false,
                hasBeenActivated: true,
                modalScopeDepth: -1,
                isDeactivated: true));
    }
}
