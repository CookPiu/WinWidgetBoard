using System.Globalization;
using System.Text.Json;

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
        string conditionText,
        string humidityText,
        string windText,
        string observedAtText,
        string attributionText,
        string attributionUrl,
        bool hasData)
    {
        LocationLabel = locationLabel;
        TemperatureText = temperatureText;
        ApparentTemperatureText = apparentTemperatureText;
        ConditionText = conditionText;
        HumidityText = humidityText;
        WindText = windText;
        ObservedAtText = observedAtText;
        AttributionText = attributionText;
        AttributionUrl = attributionUrl;
        HasData = hasData;
    }

    public string LocationLabel { get; }

    public string TemperatureText { get; }

    public string ApparentTemperatureText { get; }

    public string ConditionText { get; }

    public string HumidityText { get; }

    public string WindText { get; }

    public string ObservedAtText { get; }

    public string AttributionText { get; }

    public string AttributionUrl { get; }

    public bool HasData { get; }

    public static WeatherCardProjection Empty { get; } = new(
        "Weather",
        "—",
        "—",
        "—",
        "—",
        "—",
        "—",
        "Weather data by Open-Meteo.com",
        "https://open-meteo.com/",
        hasData: false);

    public static WeatherCardProjection FromSnapshot(
        CardRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        JsonElement payload = snapshot.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        string locationLabel = ReadString(
                payload,
                "location",
                "label") ??
            "Weather";
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
                "—",
                attributionText,
                attributionUrl,
                hasData: false);
        }

        string temperatureText = FormatTemperature(
            current,
            "temperatureC");
        bool hasData = temperatureText != "—";

        return new WeatherCardProjection(
            locationLabel,
            temperatureText,
            FormatTemperature(current, "apparentTemperatureC"),
            DescribeWeatherCode(ReadInt32(current, "weatherCode")),
            FormatPercentage(current, "relativeHumidityPercent"),
            FormatSpeed(current, "windSpeedKmh"),
            ReadString(current, "observedAtLocal") ?? "—",
            attributionText,
            attributionUrl,
            hasData);
    }

    private static string FormatTemperature(
        JsonElement current,
        string propertyName)
    {
        return current.TryGetProperty(propertyName, out JsonElement value) &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number)
            ? $"{number.ToString("0.#", CultureInfo.CurrentCulture)} °C"
            : "—";
    }

    private static string FormatPercentage(
        JsonElement current,
        string propertyName)
    {
        return current.TryGetProperty(propertyName, out JsonElement value) &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number)
            ? $"{number.ToString("0.#", CultureInfo.CurrentCulture)}%"
            : "—";
    }

    private static string FormatSpeed(
        JsonElement current,
        string propertyName)
    {
        return current.TryGetProperty(propertyName, out JsonElement value) &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number)
            ? $"{number.ToString("0.#", CultureInfo.CurrentCulture)} km/h"
            : "—";
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
        value.TryGetInt32(out int result)
            ? result
            : null;

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
