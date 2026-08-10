using System.Globalization;

namespace WinWidgetBoard.Contracts.Protocol;

public readonly record struct ProtocolVersion(int Major, int Minor)
{
    public static bool TryParse(string? text, out ProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            major < 0 ||
            minor < 0 ||
            text != $"{major}.{minor}")
        {
            return false;
        }

        version = new ProtocolVersion(major, minor);
        return true;
    }

    public static bool IsSupported(ProtocolVersionRange? range)
    {
        if (range is null ||
            !TryParse(range.Min, out ProtocolVersion minimum) ||
            !TryParse(range.Max, out ProtocolVersion maximum))
        {
            return false;
        }

        ProtocolVersion current = new(
            ProtocolConstants.CurrentMajor,
            ProtocolConstants.CurrentMinor);
        return minimum.Major == current.Major &&
            maximum.Major == current.Major &&
            Compare(minimum, current) <= 0 &&
            Compare(maximum, current) >= 0;
    }

    private static int Compare(ProtocolVersion left, ProtocolVersion right)
    {
        int major = left.Major.CompareTo(right.Major);
        return major != 0 ? major : left.Minor.CompareTo(right.Minor);
    }
}
