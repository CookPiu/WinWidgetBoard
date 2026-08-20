namespace WinWidgetBoard.Contracts.Protocol;

/// <summary>
/// The WMO weather code reduced to the small set of conditions the product actually draws.
/// It lives in Contracts because three processes have to agree on it: CoreBroker produces the
/// token, the panel picks a card illustration from it, and the native taskbar entry picks a
/// glyph from it. A raw WMO code would force every one of them to carry its own copy of this
/// table, which is exactly how two of them would end up disagreeing.
/// </summary>
public static class WeatherConditionContract
{
    public const string ClearDay = "clear-day";
    public const string ClearNight = "clear-night";
    public const string PartlyCloudyDay = "partly-cloudy-day";
    public const string PartlyCloudyNight = "partly-cloudy-night";
    public const string Cloudy = "cloudy";
    public const string Fog = "fog";
    public const string Drizzle = "drizzle";
    public const string Rain = "rain";
    public const string Snow = "snow";
    public const string Thunderstorm = "thunderstorm";
    public const string Unknown = "unknown";

    public const int MaxConditionIconIdLength = 32;

    public static IReadOnlyList<string> All { get; } =
    [
        ClearDay,
        ClearNight,
        PartlyCloudyDay,
        PartlyCloudyNight,
        Cloudy,
        Fog,
        Drizzle,
        Rain,
        Snow,
        Thunderstorm,
        Unknown,
    ];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Maps a WMO weather interpretation code to a drawable condition. Codes outside the
    /// documented set collapse to <see cref="Unknown"/> rather than guessing a nearby one.
    /// </summary>
    public static string FromWeatherCode(int code, bool isDay) =>
        code switch
        {
            0 => isDay ? ClearDay : ClearNight,
            1 or 2 => isDay ? PartlyCloudyDay : PartlyCloudyNight,
            3 => Cloudy,
            45 or 48 => Fog,
            51 or 53 or 55 or 56 or 57 => Drizzle,
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => Rain,
            71 or 73 or 75 or 77 or 85 or 86 => Snow,
            95 or 96 or 99 => Thunderstorm,
            _ => Unknown,
        };
}
