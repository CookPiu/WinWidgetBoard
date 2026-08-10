using System.Globalization;

namespace WinWidgetBoard.WorkspacePanel.Shell;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsValid => Right > Left && Bottom > Top;

    public bool Contains(ScreenRect other) =>
        IsValid && other.IsValid &&
        other.Left >= Left &&
        other.Top >= Top &&
        other.Right <= Right &&
        other.Bottom <= Bottom;

    public int CenterX => Left + Width / 2;

    public int CenterY => Top + Height / 2;

    public override string ToString() => $"[{Left},{Top} - {Right},{Bottom}]";

    public static bool TryParse(string? value, out ScreenRect rectangle)
    {
        rectangle = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int right) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bottom))
        {
            return false;
        }

        rectangle = new ScreenRect(left, top, right, bottom);
        return rectangle.IsValid;
    }
}

public sealed record PanelLaunchContext(
    ScreenRect MonitorRect,
    ScreenRect WorkAreaRect,
    ScreenRect LauncherRect,
    uint Dpi)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out PanelLaunchContext? context,
        out string? error)
    {
        context = null;
        error = null;

        string? monitorValue = FindValue(arguments, "--monitor-rect=");
        string? workAreaValue = FindValue(arguments, "--work-area=");
        string? launcherValue = FindValue(arguments, "--launcher-rect=");
        string? dpiValue = FindValue(arguments, "--dpi=");
        bool hasContextArguments = monitorValue is not null ||
            workAreaValue is not null ||
            launcherValue is not null ||
            dpiValue is not null;

        if (!hasContextArguments)
        {
            return true;
        }

        if (!ScreenRect.TryParse(monitorValue, out ScreenRect monitorRect) ||
            !ScreenRect.TryParse(workAreaValue, out ScreenRect workAreaRect) ||
            !ScreenRect.TryParse(launcherValue, out ScreenRect launcherRect))
        {
            error = "panel launch context requires valid monitor, work-area and launcher rectangles";
            return false;
        }

        if (!uint.TryParse(dpiValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dpi) ||
            dpi is < 48 or > 768)
        {
            error = "panel launch context requires a DPI value between 48 and 768";
            return false;
        }

        context = new PanelLaunchContext(monitorRect, workAreaRect, launcherRect, dpi);
        return true;
    }

    private static string? FindValue(IReadOnlyList<string> arguments, string prefix)
    {
        foreach (string argument in arguments)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }
}
