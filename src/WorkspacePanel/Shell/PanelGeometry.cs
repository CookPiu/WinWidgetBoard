namespace WinWidgetBoard.WorkspacePanel.Shell;

public readonly record struct PanelPlacement(
    ScreenRect WindowRect,
    uint Dpi,
    bool IsValid,
    string Reason);

public static class PanelGeometry
{
    private const uint DefaultDpi = 96;
    private const int MinimumWidthLogical = 680;
    private const int MaximumWidthLogical = 960;
    private const int MinimumHeightLogical = 560;
    private const int EdgeGapLogical = 10;

    public static PanelPlacement Calculate(PanelLaunchContext context)
    {
        uint dpi = context.Dpi == 0 ? DefaultDpi : context.Dpi;
        if (!context.MonitorRect.IsValid ||
            !context.WorkAreaRect.IsValid ||
            !context.MonitorRect.Contains(context.WorkAreaRect))
        {
            return Invalid(dpi, "monitor/work-area rectangles are invalid");
        }

        int gap = Scale(EdgeGapLogical, dpi);
        int minimumWidth = Scale(MinimumWidthLogical, dpi);
        int maximumWidth = Scale(MaximumWidthLogical, dpi);
        int minimumHeight = Scale(MinimumHeightLogical, dpi);
        int availableWidth = context.WorkAreaRect.Width - gap * 2;
        int availableHeight = context.WorkAreaRect.Height - gap * 2;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return Invalid(dpi, "work area is too small for panel spacing");
        }

        int width = ClampDimension(
            (int)Math.Round(context.WorkAreaRect.Width * 0.46, MidpointRounding.AwayFromZero),
            minimumWidth,
            maximumWidth,
            availableWidth);
        int height = ClampDimension(
            (int)Math.Round(context.WorkAreaRect.Height * 0.90, MidpointRounding.AwayFromZero),
            minimumHeight,
            context.WorkAreaRect.Height,
            availableHeight);
        if (width <= 0 || height <= 0)
        {
            return Invalid(dpi, "computed panel dimensions are invalid");
        }

        bool hasLauncherAnchor =
            context.LauncherRect.IsValid &&
            context.MonitorRect.Contains(context.LauncherRect);
        bool expandFromLeft = !hasLauncherAnchor ||
            context.LauncherRect.CenterX <= context.WorkAreaRect.CenterX;
        bool expandFromTop = hasLauncherAnchor &&
            context.LauncherRect.CenterY <= context.WorkAreaRect.CenterY;

        int left = expandFromLeft
            ? context.WorkAreaRect.Left + gap
            : context.WorkAreaRect.Right - gap - width;
        int top = expandFromTop
            ? context.WorkAreaRect.Top + gap
            : context.WorkAreaRect.Bottom - gap - height;
        ScreenRect windowRect = new(
            left,
            top,
            left + width,
            top + height);

        if (!context.WorkAreaRect.Contains(windowRect))
        {
            return Invalid(dpi, "computed panel rectangle escapes the work area");
        }

        return new PanelPlacement(
            windowRect,
            dpi,
            true,
            hasLauncherAnchor
                ? "panel anchored to the launcher corner"
                : "panel uses the default bottom-left work-area anchor");
    }

    public static bool RunContractSmokeTest(out string failure)
    {
        PanelLaunchContext context = new(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1920, 1032),
            new ScreenRect(8, 976, 44, 1012),
            DefaultDpi);
        PanelPlacement normal = Calculate(context);
        if (!normal.IsValid || !context.WorkAreaRect.Contains(normal.WindowRect))
        {
            failure = "normal bottom-left context did not produce an in-bounds panel";
            return false;
        }

        PanelPlacement highDpi = Calculate(context with { Dpi = 192 });
        if (!highDpi.IsValid ||
            highDpi.WindowRect.Width <= normal.WindowRect.Width ||
            highDpi.WindowRect.Height <= normal.WindowRect.Height)
        {
            failure = "high-DPI panel dimensions did not scale";
            return false;
        }

        PanelPlacement invalid = Calculate(context with
        {
            WorkAreaRect = new ScreenRect(0, 0, 0, 0),
        });
        if (invalid.IsValid)
        {
            failure = "invalid work-area geometry was accepted";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static PanelPlacement Invalid(uint dpi, string reason) =>
        new(default, dpi, false, reason);

    private static int ClampDimension(int desired, int minimum, int maximum, int available)
    {
        int upperBound = Math.Min(maximum, available);
        int lowerBound = Math.Min(minimum, upperBound);
        return Math.Clamp(desired, lowerBound, upperBound);
    }

    private static int Scale(int logicalPixels, uint dpi) =>
        (int)Math.Round(logicalPixels * dpi / (double)DefaultDpi, MidpointRounding.AwayFromZero);
}
