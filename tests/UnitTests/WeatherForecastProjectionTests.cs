using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WeatherForecastProjectionTests
{
    private const string PayloadWithForecast = """
        {
          "attribution": "Weather data by Open-Meteo.com",
          "attributionUrl": "https://open-meteo.com/",
          "location": { "label": "Beijing" },
          "current": {
            "temperatureC": 30.9,
            "apparentTemperatureC": 33.1,
            "relativeHumidityPercent": 54,
            "windSpeedKmh": 9.4,
            "weatherCode": 3,
            "conditionIconId": "cloudy",
            "observedAtLocal": "2026-08-25T09:00"
          },
          "hourly": [
            { "timeLocal": "2026-08-25T09:00", "temperatureC": 30.9, "conditionIconId": "cloudy" },
            { "timeLocal": "2026-08-25T10:00", "temperatureC": 31.6, "conditionIconId": "rain" }
          ],
          "daily": [
            { "dateLocal": "2026-08-26", "highTemperatureC": 24.6, "lowTemperatureC": 19.2, "conditionIconId": "rain" },
            { "dateLocal": "2026-08-27", "highTemperatureC": 29.4, "lowTemperatureC": 20.1, "conditionIconId": "clear-day" }
          ]
        }
        """;

    [TestMethod(DisplayName =
        "UT-WEA-020 [WEA-001] Forecast projects to bounded rows with their own condition tokens")]
    public void ForecastProjectsToBoundedRows()
    {
        WeatherCardProjection projection = Project(PayloadWithForecast);

        Assert.IsTrue(projection.HasHours);
        Assert.IsTrue(projection.HasDays);
        Assert.AreEqual(2, projection.Hours.Count);
        Assert.AreEqual(2, projection.Days.Count);

        // The number travels beside the text: the card draws the row from the string and
        // would have to re-parse it to plot anything, which would make the display format
        // part of the geometry.
        Assert.AreEqual(30.9, projection.Hours[0].TemperatureCelsius, 0.001);
        Assert.AreEqual("rain", projection.Hours[1].ConditionIconId);
        Assert.AreEqual("clear-day", projection.Days[1].ConditionIconId);
        Assert.IsTrue(projection.Days[0].HighTemperatureText.Contains("24.6", StringComparison.Ordinal));
        Assert.IsTrue(projection.Days[0].LowTemperatureText.Contains("19.2", StringComparison.Ordinal));
    }

    [TestMethod(DisplayName =
        "UT-WEA-021 [WEA-001] A payload with no forecast still projects the current reading")]
    public void APayloadWithNoForecastStillProjectsTheCurrentReading()
    {
        WeatherCardProjection projection = Project("""
            {
              "location": { "label": "Beijing" },
              "current": { "temperatureC": 30.9, "observedAtLocal": "2026-08-25T09:00" }
            }
            """);

        // The forecast is supplementary. A card that has the current reading is a working
        // card, and the absence of a trend must not be reported as a failure.
        Assert.IsTrue(projection.HasData);
        Assert.IsFalse(projection.HasHours);
        Assert.IsFalse(projection.HasDays);
    }

    [TestMethod(DisplayName =
        "UT-WEA-022 [WEA-001] A malformed forecast entry ends the list instead of failing the card")]
    public void AMalformedForecastEntryEndsTheList()
    {
        WeatherCardProjection projection = Project("""
            {
              "location": { "label": "Beijing" },
              "current": { "temperatureC": 30.9, "observedAtLocal": "2026-08-25T09:00" },
              "hourly": [
                { "timeLocal": "2026-08-25T09:00", "temperatureC": 30.9, "conditionIconId": "cloudy" },
                { "timeLocal": "2026-08-25T10:00", "temperatureC": "not a number" }
              ],
              "daily": [
                { "dateLocal": "2026-08-26", "highTemperatureC": 24.6, "lowTemperatureC": 19.2 },
                { "dateLocal": "2026-08-27" }
              ]
            }
            """);

        Assert.IsTrue(projection.HasData);
        Assert.AreEqual(1, projection.Hours.Count);
        Assert.AreEqual(1, projection.Days.Count);
    }

    [TestMethod(DisplayName =
        "UT-WEA-023 [WEA-001] With no reading the card has no location to name")]
    public void WithNoReadingTheCardHasNoLocationToName()
    {
        // It used to fall back to the literal "Weather", which is an English string in a
        // localized UI and reads as a place name: the card's location line said "Weather"
        // until the first fetch returned.
        Assert.AreEqual(string.Empty, WeatherCardProjection.Empty.LocationLabel);
        Assert.IsFalse(WeatherCardProjection.Empty.HasHours);
        Assert.IsFalse(WeatherCardProjection.Empty.HasDays);
    }

    private static WeatherCardProjection Project(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        var snapshot = new CardRuntimeSnapshot(
            "demo.weather",
            "builtin.weather",
            schemaVersion: 1,
            sequence: 1,
            DateTimeOffset.UtcNow,
            CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Ready,
            document.RootElement,
            [CardRuntimeActionIds.Refresh]);
        return WeatherCardProjection.FromSnapshot(snapshot);
    }
}
