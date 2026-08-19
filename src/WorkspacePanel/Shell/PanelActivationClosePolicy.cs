namespace WinWidgetBoard.WorkspacePanel.Shell;

internal static class PanelActivationClosePolicy
{
    internal static bool ShouldRequestClose(
        bool keepOpenForAcceptance,
        bool hasBeenActivated,
        int modalScopeDepth,
        bool isDeactivated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(modalScopeDepth);

        return isDeactivated &&
            !keepOpenForAcceptance &&
            hasBeenActivated &&
            modalScopeDepth == 0;
    }

    // An explicit close request can land while a dialog is still tearing down: the dialog has
    // already left the visual tree but ShowAsync has not returned, so the modal scope is still
    // held. Defer the request instead of dropping it, and replay it once the scope is released.
    internal static bool ShouldDeferCloseRequest(int modalScopeDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(modalScopeDepth);

        return modalScopeDepth > 0;
    }

    internal static bool ShouldReplayDeferredClose(
        int modalScopeDepth,
        bool hasDeferredCloseRequest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(modalScopeDepth);

        return hasDeferredCloseRequest && modalScopeDepth == 0;
    }
}
