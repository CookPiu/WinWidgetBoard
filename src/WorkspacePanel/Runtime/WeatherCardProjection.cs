using System.Globalization;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// UI-facing projection of the weather payload. The UI binds to bounded
/// strings instead of reaching into JsonElement or provider-specific fields.
/// </summary>
public sealed record WeatherCardProjection
{
    private WeatherCardProjection(
        string locationLabel,
        string temperatureText,
        string apparentTemperatureText,
        string highTemperatureText,
        string lowTemperatureText,
        string conditionText,
        string conditionResourceKey,
        string conditionIconId,
        string humidityText,
        string windText,
        string observedAtText,
        string attributionText,
        string attributionUrl,
        bool hasData,
        IReadOnlyList<WeatherHourProjection> hours,
        IReadOnlyList<WeatherDayProjection> days)
    {
        LocationLabel = locationLabel;
        TemperatureText = temperatureText;
        ApparentTemperatureText = apparentTemperatureText;
        HighTemperatureText = highTemperatureText;
        LowTemperatureText = lowTemperatureText;
        ConditionText = conditionText;
        ConditionResourceKey = conditionResourceKey;
        ConditionIconId = conditionIconId;
        HumidityText = humidityText;
        WindText = windText;
        ObservedAtText = observedAtText;
        AttributionText = attributionText;
        AttributionUrl = attributionUrl;
        HasData = hasData;
        Hours = hours;
        Days = days;
    }

    public string LocationLabel { get; }

    public string TemperatureText { get; }

    public string ApparentTemperatureText { get; }

    /// <summary>
    /// Today's high, already formatted. The provider publishes it beside the current reading
    /// because it describes the same day; the forecast rows start tomorrow.
    /// </summary>
    public string HighTemperatureText { get; }

    public string LowTemperatureText { get; }

    public string ConditionText { get; }

    /// <summary>
    /// The resource key for <see cref="ConditionText"/>, so the card can say "多云" in a
    /// Chinese UI. The projection stays WinUI-free and therefore cannot resolve it itself;
    /// it names the string, and the surface item - which already carries the panel's resource
    /// resolver - looks it up and falls back to the English text when a key has no entry.
    /// </summary>
    public string ConditionResourceKey { get; }

    /// <summary>
    /// A <see cref="WeatherConditionContract"/> token. The card picks its illustration from
    /// this rather than from the WMO code, so the panel, the broker and the taskbar entry
    /// all read the same reduced condition set.
    /// </summary>
    public string ConditionIconId { get; }

    public string HumidityText { get; }

    public string WindText { get; }

    public string ObservedAtText { get; }

    public string AttributionText { get; }

    public string AttributionUrl { get; }

    public bool HasData { get; }

    /// <summary>
    /// The next few hours, oldest first. Empty when the provider sent none, which the card
    /// reads as "no trend to draw" rather than as an error.
    /// </summary>
    public IReadOnlyList<WeatherHourProjection> Hours { get; }

    /// <summary>The days after today, nearest first. Empty when the provider sent none.</summary>
    public IReadOnlyList<WeatherDayProjection> Days { get; }

    public bool HasHours => Hours.Count > 0;

    public bool HasDays => Days.Count > 0;

    /// <summary>
    /// True only when both ends of today's range are readable. Half a range - a high with no
    /// low - reads as a second current temperature, so the card shows the pair or neither.
    /// </summary>
    public bool HasHighLow =>
        HighTemperatureText != "—" && LowTemperatureText != "—";

    /// <summary>
    /// The label is empty rather than a placeholder word. It used to fall back to the literal
    /// "Weather", which is an English string in a localized UI and, worse, reads as a location:
    /// the card's location line said "Weather" until the first fetch came back. Empty lets the
    /// card leave the line out until there is a place to name.
    /// </summary>
    public static WeatherCardProjection Empty { get; } = new(
        string.Empty,
        "—",
        "—",
        "—",
        "—",
        "—",
        string.Empty,
        WeatherConditionContract.Unknown,
        "—",
        "—",
        "—",
        "Weather data by Open-Meteo.com",
        "https://open-meteo.com/",
        hasData: false,
        [],
        []);

    public static WeatherCardProjection FromSnapshot(
        CardRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        JsonElement payload = snapshot.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        // The payload's numbers are always metric; this tag is the reader's chosen units,
        // applied here because formatting is the last moment a number is still a number.
        bool imperial = string.Equals(
            ReadString(payload, "unitSystem"),
            WeatherSettingsContract.ImperialUnitSystem,
            StringComparison.Ordinal);
        string locationLabel = ReadString(
                payload,
                "location",
                "label") ??
            Empty.LocationLabel;
        string attributionText = ReadString(
                payload,
                "attribution") ??
            Empty.AttributionText;
        string attributionUrl = ReadString(
            payload,
            "attributionUrl") ??
            Empty.AttributionUrl;
        if (!payload.TryGetProperty("current", out JsonElement current) ||
            current.ValueKind != JsonValueKind.Object)
        {
            return new WeatherCardProjection(
                locationLabel,
                "—",
                "—",
                "—",
                "—",
                "—",
                string.Empty,
                WeatherConditionContract.Unknown,
                "—",
                "—",
                "—",
                attributionText,
                attributionUrl,
                hasData: false,
                [],
                []);
        }

        string temperatureText = FormatTemperature(
            current,
            "temperatureC",
            imperial);
        bool hasData = temperatureText != "—";

        return new WeatherCardProjection(
            locationLabel,
            temperatureText,
            FormatTemperature(current, "apparentTemperatureC", imperial),
            FormatTemperature(current, "todayHighTemperatureC", imperial),
            FormatTemperature(current, "todayLowTemperatureC", imperial),
            DescribeWeatherCode(ReadInt32(current, "weatherCode")),
            ResolveConditionResourceKey(ReadInt32(current, "weatherCode")),
            ReadConditionIconId(current),
            FormatPercentage(current, "relativeHumidityPercent"),
            FormatSpeed(current, "windSpeedKmh", imperial),
            FormatObservedAt(ReadString(current, "observedAtLocal")),
            attributionText,
            attributionUrl,
            hasData,
            ReadHours(payload, imperial),
            ReadDays(payload, imperial));
    }

    /// <summary>
    /// The hourly trend. A malformed entry ends the list rather than failing the projection:
    /// the current reading is the card's job, and a broken trend must not take it down with it.
    /// </summary>
    private static List<WeatherHourProjection> ReadHours(
        JsonElement payload,
        bool imperial)
    {
        var hours = new List<WeatherHourProjection>();
        if (!payload.TryGetProperty("hourly", out JsonElement hourly) ||
            hourly.ValueKind != JsonValueKind.Array)
        {
            return hours;
        }

        foreach (JsonElement entry in hourly.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !TryReadFiniteDouble(entry, "temperatureC", out double celsius))
            {
                break;
            }

            hours.Add(
                new WeatherHourProjection(
                    FormatHourLabel(ReadString(entry, "timeLocal")),
                    FormatTemperature(entry, "temperatureC", imperial),
                    celsius,
                    ReadConditionIconId(entry)));
        }

        return hours;
    }

    private static List<WeatherDayProjection> ReadDays(
        JsonElement payload,
        bool imperial)
    {
        var days = new List<WeatherDayProjection>();
        if (!payload.TryGetProperty("daily", out JsonElement daily) ||
            daily.ValueKind != JsonValueKind.Array)
        {
            return days;
        }

        foreach (JsonElement entry in daily.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            string high = FormatTemperature(entry, "highTemperatureC", imperial);
            string low = FormatTemperature(entry, "lowTemperatureC", imperial);
            if (high == "—" || low == "—")
            {
                break;
            }

            days.Add(
                new WeatherDayProjection(
                    FormatDayLabel(ReadString(entry, "dateLocal")),
                    high,
                    low,
                    ReadConditionIconId(entry)));
        }

        return days;
    }

    /// <summary>
    /// "14:00" from the provider's local ISO timestamp. Formatted with the current culture's
    /// short time so a 12-hour locale does not read a 24-hour clock, and falls back to the raw
    /// text rather than inventing a time when the string is not a timestamp.
    /// </summary>
    private static string FormatHourLabel(string? timeLocal)
    {
        if (timeLocal is null)
        {
            return "—";
        }

        return DateTime.TryParse(
            timeLocal,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed.ToString("HH:mm", CultureInfo.CurrentCulture)
            : timeLocal;
    }

    /// <summary>
    /// The observation time as a clock reading. The provider sends a local ISO timestamp,
    /// which the card was showing verbatim - "2026-08-25T09:30" is a machine's way of saying
    /// half past nine.
    /// </summary>
    private static string FormatObservedAt(string? observedAtLocal)
    {
        if (observedAtLocal is null)
        {
            return "—";
        }

        return DateTime.TryParse(
            observedAtLocal,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed.ToString("t", CultureInfo.CurrentCulture)
            : observedAtLocal;
    }

    /// <summary>The weekday, which is what a three-day forecast is actually read by.</summary>
    private static string FormatDayLabel(string? dateLocal)
    {
        if (dateLocal is null)
        {
            return "—";
        }

        return DateTime.TryParse(
            dateLocal,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed.ToString("ddd", CultureInfo.CurrentCulture)
            : dateLocal;
    }

    /// <summary>
    /// Reads a finite number, or reports that there is not one. The kind is checked before
    /// the value: <see cref="JsonElement.TryGetDouble"/> throws on a string rather than
    /// returning false, so a payload field of the wrong type would take the whole card down
    /// instead of leaving one reading blank.
    /// </summary>
    private static bool TryReadFiniteDouble(
        JsonElement parent,
        string propertyName,
        out double value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static string FormatTemperature(
        JsonElement current,
        string propertyName,
        bool imperial)
    {
        if (!TryReadFiniteDouble(current, propertyName, out double number))
        {
            return "—";
        }

        if (imperial)
        {
            number = (number * 9d / 5d) + 32d;
        }

        string unit = imperial ? "°F" : "°C";
        return $"{number.ToString("0.#", CultureInfo.CurrentCulture)} {unit}";
    }

    private static string FormatPercentage(
        JsonElement current,
        string propertyName)
    {
        return TryReadFiniteDouble(current, propertyName, out double number)
            ? $"{number.ToString("0.#", CultureInfo.CurrentCulture)}%"
            : "—";
    }

    private static string FormatSpeed(
        JsonElement current,
        string propertyName,
        bool imperial)
    {
        if (!TryReadFiniteDouble(current, propertyName, out double number))
        {
            return "—";
        }

        if (imperial)
        {
            number /= 1.609344d;
        }

        string unit = imperial ? "mph" : "km/h";
        return $"{number.ToString("0.#", CultureInfo.CurrentCulture)} {unit}";
    }

    private static string? ReadString(
        JsonElement parent,
        string propertyName,
        string? nestedPropertyName = null)
    {
        if (nestedPropertyName is not null)
        {
            if (!parent.TryGetProperty(propertyName, out JsonElement nested) ||
                nested.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            parent = nested;
            propertyName = nestedPropertyName;
        }

        return parent.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt32(
        JsonElement parent,
        string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    /// <summary>
    /// Prefers the token the provider already produced and falls back to deriving one from
    /// the WMO code, so a payload from an older provider build still gets an illustration.
    /// An unrecognised token is treated as absent rather than trusted.
    /// </summary>
    private static string ReadConditionIconId(JsonElement current)
    {
        if (current.TryGetProperty("conditionIconId", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            WeatherConditionContract.IsKnown(value.GetString()))
        {
            return value.GetString()!;
        }

        int? code = ReadInt32(current, "weatherCode");
        if (code is null)
        {
            return WeatherConditionContract.Unknown;
        }

        bool isDay = !current.TryGetProperty("isDay", out JsonElement isDayValue) ||
            isDayValue.ValueKind != JsonValueKind.False;
        return WeatherConditionContract.FromWeatherCode(code.Value, isDay);
    }

    /// <summary>
    /// The resource key for a WMO code. Empty for a code with no localized name, which the
    /// card reads as "use the English description" rather than as a missing string.
    /// </summary>
    private static string ResolveConditionResourceKey(int? code) =>
        code switch
        {
            0 => "WeatherConditionClearSky",
            1 => "WeatherConditionMainlyClear",
            2 => "WeatherConditionPartlyCloudy",
            3 => "WeatherConditionOvercast",
            45 or 48 => "WeatherConditionFog",
            51 or 53 or 55 => "WeatherConditionDrizzle",
            56 or 57 => "WeatherConditionFreezingDrizzle",
            61 or 63 or 65 => "WeatherConditionRain",
            66 or 67 => "WeatherConditionFreezingRain",
            71 or 73 or 75 or 77 => "WeatherConditionSnow",
            80 or 81 or 82 => "WeatherConditionRainShowers",
            85 or 86 => "WeatherConditionSnowShowers",
            95 => "WeatherConditionThunderstorm",
            96 or 99 => "WeatherConditionThunderstormHail",
            _ => string.Empty,
        };

    private static string DescribeWeatherCode(int? code) =>
        code switch
        {
            0 => "Clear sky",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing drizzle",
            61 or 63 or 65 => "Rain",
            66 or 67 => "Freezing rain",
            71 or 73 or 75 or 77 => "Snow",
            80 or 81 or 82 => "Rain showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Thunderstorm with hail",
            { } value => $"WMO {value.ToString(CultureInfo.InvariantCulture)}",
            null => "—",
        };
}

/// <summary>
/// One hour of the trend. The temperature is carried both as the text the row shows and as the
/// number the trend line is plotted from - the card needs both, and re-parsing the string to
/// get the number back would make the display format part of the geometry.
/// </summary>
public sealed record WeatherHourProjection(
    string TimeText,
    string TemperatureText,
    double TemperatureCelsius,
    string ConditionIconId);

/// <summary>One day of the forecast, high and low already formatted.</summary>
public sealed record WeatherDayProjection(
    string DayText,
    string HighTemperatureText,
    string LowTemperatureText,
    string ConditionIconId);
