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
}
