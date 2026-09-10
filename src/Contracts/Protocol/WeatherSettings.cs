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

    // Which network source the card reads from. Stored and sent as a short token rather than
    // as the scheduler's provider id: the scheduler id names a code path, this names a user
    // choice, and the two are free to drift.
    public const string OpenMeteoProviderId = "open-meteo";
    public const string QWeatherProviderId = "qweather";

    public const int MaxApiHostLength = 100;
    public const int MaxApiKeyLength = 128;

    /// <summary>
    /// The hosts an API key is allowed to travel to. A key is a bearer credential, so the
    /// host it is sent to is part of the security boundary, not a free-form setting: a
    /// mistyped or pasted-in host must not be able to forward the key to a third party.
    /// </summary>
    public static IReadOnlyList<string> QWeatherHostSuffixes { get; } =
    [
        ".qweatherapi.com",
        ".qweather.com",
    ];

    public static IReadOnlyList<string> ProviderIds { get; } =
    [
        OpenMeteoProviderId,
        QWeatherProviderId,
    ];

    public static bool IsValidProviderId(string? value) =>
        value is not null && ProviderIds.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Missing means Open-Meteo, so a row written before a second source existed - and a
    /// request from a client that has never heard of the field - keeps meaning what it meant.
    /// </summary>
    public static bool TryNormalizeProviderId(string? value, out string normalized)
    {
        normalized = OpenMeteoProviderId;
        if (value is null)
        {
            return true;
        }

        if (!IsValidProviderId(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    public static bool RequiresApiCredential(string? providerId) =>
        string.Equals(providerId, QWeatherProviderId, StringComparison.Ordinal);

    /// <summary>
    /// Reduces what the user pasted to a bare lowercase host name. A full URL is accepted
    /// because the console shows the host inside one, but only its host survives: a path,
    /// a query, a port or a userinfo section would each be a different destination from the
    /// one <see cref="IsAllowedQWeatherHost"/> checked.
    /// </summary>
    public static bool TryNormalizeApiHost(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return true;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) ||
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
                parsed.UserInfo.Length != 0 ||
                !parsed.IsDefaultPort)
            {
                return false;
            }

            trimmed = parsed.Host;
        }

        trimmed = trimmed.ToLowerInvariant();
        if (trimmed.Length > MaxApiHostLength || !IsHostName(trimmed))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static bool IsAllowedQWeatherHost(string? host) =>
        host is not null &&
        QWeatherHostSuffixes.Any(
            suffix => host.EndsWith(suffix, StringComparison.Ordinal) &&
                host.Length > suffix.Length);

    /// <summary>
    /// An API key is opaque to us, so it is only checked for the shape a header value can
    /// carry: printable ASCII, no spaces, bounded. Null means "leave the stored key alone"
    /// and is the caller's business, not this method's.
    /// </summary>
    public static bool IsValidApiKey(string? value) =>
        value is not null &&
        value.Length is > 0 and <= MaxApiKeyLength &&
        value.All(static character => character is > ' ' and < (char)0x7f);

    private static bool IsHostName(string value)
    {
        if (value.Length == 0 ||
            value.StartsWith('.') ||
            value.EndsWith('.') ||
            !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string label in value.Split('.'))
        {
            if (label.Length is 0 or > 63 ||
                label.StartsWith('-') ||
                label.EndsWith('-') ||
                !label.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return false;
            }
        }

        return true;
    }

    // Display units. The payload's numbers stay metric on the wire - "temperatureC" keeps
    // meaning Celsius no matter what the user reads - and both composers (the broker's entry
    // summary and the panel's card projection) convert at the last moment. Metric is the
    // default because it is what every existing row and every older client means by silence.
    public const string MetricUnitSystem = "metric";
    public const string ImperialUnitSystem = "imperial";

    public static bool IsValidUnitSystem(string? value) =>
        string.Equals(value, MetricUnitSystem, StringComparison.Ordinal) ||
        string.Equals(value, ImperialUnitSystem, StringComparison.Ordinal);

    /// <summary>
    /// Missing means metric: an older client that has never heard of units keeps meaning
    /// exactly what it meant before the field existed. Anything else must be a known token.
    /// </summary>
    public static bool TryNormalizeUnitSystem(string? value, out string normalized)
    {
        normalized = MetricUnitSystem;
        if (value is null)
        {
            return true;
        }

        if (!IsValidUnitSystem(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

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

    // Null means metric, so a request from before this field existed keeps its meaning.
    public string? UnitSystem { get; init; }

    // Null means Open-Meteo, for the same reason.
    public string? ProviderId { get; init; }

    // The account's own API host. Null or empty clears it, which is what switching back to a
    // keyless source means.
    public string? ApiHost { get; init; }

    /// <summary>
    /// Write-only credential. Null leaves the stored key untouched - the settings dialog
    /// cannot round-trip a key it was never given - an empty string clears it, and any other
    /// value replaces it. It is never echoed back in a response; callers learn only whether
    /// a key is present, from <see cref="WeatherSettingsDto.HasApiCredential"/>.
    /// </summary>
    public string? ApiKey { get; init; }

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

    public string UnitSystem { get; init; } = WeatherSettingsContract.MetricUnitSystem;

    public string ProviderId { get; init; } = WeatherSettingsContract.OpenMeteoProviderId;

    public string ApiHost { get; init; } = string.Empty;

    // Presence only. The key itself never leaves the broker process.
    public bool HasApiCredential { get; init; }

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
