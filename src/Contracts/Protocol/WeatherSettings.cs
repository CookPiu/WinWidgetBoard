namespace WinWidgetBoard.Contracts.Protocol;

public static class WeatherSettingsContract
{
    public const string GetMethod = "weather.settings.get";
    public const string SaveMethod = "weather.settings.save";
    // Read-only projection of the last successful weather refresh, for callers that only
    // need a short line of text and must not open a card subscription - today the native
    // taskbar entry. Weather payloads still never reach SQLite.
    public const string SummaryGetMethod = "weather.summary.get";

    public const int MaxInstanceIdLength = CardsContract.MaxInstanceIdLength;
    public const int MaxLabelLength = 80;
    public const int MaxTimestampLength = 64;
    public const string DefaultInstanceId = "demo.weather";
    public const string DefaultLabel = "Singapore";
    public const double DefaultLatitude = 1.3521;
    public const double DefaultLongitude = 103.8198;

    public const int MaxTemperatureTextLength = 16;

    public static IReadOnlyList<string> Methods { get; } =
    [
        GetMethod,
        SaveMethod,
        SummaryGetMethod,
    ];

    public static bool IsValidInstanceId(string? value) =>
        CardsContract.IsValidIdentifier(value, MaxInstanceIdLength);

    public static bool TryNormalizeLabel(
        string? value,
        out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0 ||
            trimmed.Length > MaxLabelLength ||
            trimmed.Any(char.IsControl))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static bool IsValidCoordinates(
        double latitude,
        double longitude) =>
        double.IsFinite(latitude) &&
        double.IsFinite(longitude) &&
        latitude >= -90d &&
        latitude <= 90d &&
        longitude >= -180d &&
        longitude <= 180d;
}

public sealed record WeatherSettingsGetRequest
{
    public string? InstanceId { get; init; }
}

public sealed record WeatherSettingsGetResponse
{
    public WeatherSettingsDto Settings { get; init; } = new();
}

public sealed record WeatherSettingsSaveRequest
{
    public Guid ClientOperationId { get; init; }

    public string? InstanceId { get; init; }

    public string? Label { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public bool UseDeviceLocation { get; init; } = true;

    public int ExpectedRevision { get; init; }
}

public sealed record WeatherSettingsSaveResponse
{
    public Guid ClientOperationId { get; init; }

    public WeatherSettingsDto Settings { get; init; } = new();
}

public sealed record WeatherSettingsDto
{
    public string InstanceId { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public bool UseDeviceLocation { get; init; } = true;

    public int Revision { get; init; }

    public string UpdatedAtUtc { get; init; } = string.Empty;
}

public sealed record WeatherSummaryGetRequest
{
    public string? InstanceId { get; init; }
}

public sealed record WeatherSummaryGetResponse
{
    // Absent until the first successful refresh of this process; callers render their own
    // unavailable state rather than a stale or invented value.
    public WeatherSummaryDto? Summary { get; init; }
}

public sealed record WeatherSummaryDto
{
    public string InstanceId { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    // Rounded whole degrees Celsius as text, so a minimal native client does not have to
    // parse or format a JSON number.
    public string TemperatureText { get; init; } = string.Empty;

    // A WeatherConditionContract token. The entry draws a glyph from it, which keeps the
    // entry free of any language: there is no condition wording to translate.
    public string ConditionIconId { get; init; } = WeatherConditionContract.Unknown;

    public string ObservedAtUtc { get; init; } = string.Empty;

    public bool IsStale { get; init; }
}
