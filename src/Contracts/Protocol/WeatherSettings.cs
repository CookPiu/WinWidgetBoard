namespace WinWidgetBoard.Contracts.Protocol;

public static class WeatherSettingsContract
{
    public const string GetMethod = "weather.settings.get";
    public const string SaveMethod = "weather.settings.save";

    public const int MaxInstanceIdLength = CardsContract.MaxInstanceIdLength;
    public const int MaxLabelLength = 80;
    public const int MaxTimestampLength = 64;
    public const string DefaultInstanceId = "demo.weather";
    public const string DefaultLabel = "Singapore";
    public const double DefaultLatitude = 1.3521;
    public const double DefaultLongitude = 103.8198;

    public static IReadOnlyList<string> Methods { get; } =
    [
        GetMethod,
        SaveMethod,
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

    public int Revision { get; init; }

    public string UpdatedAtUtc { get; init; } = string.Empty;
}
