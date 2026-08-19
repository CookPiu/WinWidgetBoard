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

    [TestMethod(DisplayName = "UT-PANEL-ACT-005 [PNL-004] A close request during a modal scope is deferred, not dropped")]
    public void CloseRequestDuringModalScopeIsDeferred()
    {
        Assert.IsTrue(PanelActivationClosePolicy.ShouldDeferCloseRequest(1));
        Assert.IsFalse(PanelActivationClosePolicy.ShouldDeferCloseRequest(0));
    }

    [TestMethod(DisplayName = "UT-PANEL-ACT-006 [PNL-004] A deferred close replays only after the last modal scope is released")]
    public void DeferredCloseReplaysAfterLastModalScope()
    {
        Assert.IsTrue(PanelActivationClosePolicy.ShouldReplayDeferredClose(
            modalScopeDepth: 0,
            hasDeferredCloseRequest: true));
        Assert.IsFalse(PanelActivationClosePolicy.ShouldReplayDeferredClose(
            modalScopeDepth: 1,
            hasDeferredCloseRequest: true));
        Assert.IsFalse(PanelActivationClosePolicy.ShouldReplayDeferredClose(
            modalScopeDepth: 0,
            hasDeferredCloseRequest: false));
    }

    [TestMethod(DisplayName = "UT-PANEL-ACT-007 [PNL-004] Deferral helpers reject an invalid modal depth")]
    public void DeferralHelpersRejectInvalidModalDepth()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PanelActivationClosePolicy.ShouldDeferCloseRequest(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PanelActivationClosePolicy.ShouldReplayDeferredClose(-1, true));
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
