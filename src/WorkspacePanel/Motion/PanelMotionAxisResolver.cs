using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.WorkspacePanel.Motion;

/// <summary>
/// Selects the axis of the panel edge nearest to the launcher.
/// </summary>
public static class PanelMotionAxisResolver
{
    public static PanelMotionAxis Resolve(
        PanelLaunchContext context,
        PanelPlacement placement)
    {
        ScreenRect launcher = context.LauncherRect;
        ScreenRect panel = placement.WindowRect;
        bool hasLauncherAnchor = launcher.IsValid &&
            context.MonitorRect.IsValid &&
            context.MonitorRect.Contains(launcher);
        if (!hasLauncherAnchor || !panel.IsValid)
        {
            return PanelMotionAxis.Vertical;
        }

        int launcherX = launcher.CenterX;
        int launcherY = launcher.CenterY;
        bool outsideX = launcherX < panel.Left || launcherX > panel.Right;
        bool outsideY = launcherY < panel.Top || launcherY > panel.Bottom;

        if (outsideX && !outsideY)
        {
            return PanelMotionAxis.Horizontal;
        }

        if (!outsideX && outsideY)
        {
            return PanelMotionAxis.Vertical;
        }

        if (!outsideX && !outsideY)
        {
            return PanelMotionAxis.Vertical;
        }

        int horizontalDistance = launcherX < panel.Left
            ? panel.Left - launcherX
            : launcherX - panel.Right;
        int verticalDistance = launcherY < panel.Top
            ? panel.Top - launcherY
            : launcherY - panel.Bottom;
        return horizontalDistance <= verticalDistance
            ? PanelMotionAxis.Horizontal
            : PanelMotionAxis.Vertical;
    }
}
