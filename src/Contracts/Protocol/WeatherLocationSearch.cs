namespace WinWidgetBoard.Contracts.Protocol;

/// <summary>
/// Location search for the weather settings dialog. The user types a place name and picks
/// from a list; coordinates are resolved for them instead of typed by hand.
/// </summary>
public static class WeatherLocationSearchContract
{
    public const string SearchMethod = "weather.locations.search";

    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 64;
    public const int MaxResults = 8;
    public const int MaxNameLength = WeatherSettingsContract.MaxLabelLength;
    public const int MaxRegionLength = 80;
    public const int MaxCountryCodeLength = 8;
    public const int MaxTimezoneLength = 64;

    public static bool TryNormalizeQuery(string? value, out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length < MinQueryLength ||
            trimmed.Length > MaxQueryLength ||
            trimmed.Any(char.IsControl))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }
}

public sealed record WeatherLocationSearchRequest
{
    public string? Query { get; init; }
}

public sealed record WeatherLocationSearchResponse
{
    public IReadOnlyList<WeatherLocationCandidateDto> Results { get; init; } =
        Array.Empty<WeatherLocationCandidateDto>();
}

/// <summary>
/// One place the user can choose. <see cref="Region"/> and <see cref="Country"/> exist only
/// to tell same-named places apart; the label that gets saved is composed by the caller.
/// </summary>
public sealed record WeatherLocationCandidateDto
{
    public string Name { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public string CountryCode { get; init; } = string.Empty;

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public string Timezone { get; init; } = string.Empty;
}
