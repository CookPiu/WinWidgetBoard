using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WeatherCardProjectionTests
{
    [TestMethod(DisplayName =
        "UT-WEA-007 [WEA-001/CRD-001] weather payload projects to bounded UI values")]
    public void ValidPayloadProjectsTemperatureAndCondition()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"attribution\":\"Weather data by Open-Meteo.com\",\"attributionUrl\":\"https://open-meteo.com/\",\"location\":{\"label\":\"Singapore\"},\"current\":{\"temperatureC\":31.2,\"apparentTemperatureC\":36.4,\"relativeHumidityPercent\":72,\"windSpeedKmh\":11.5,\"weatherCode\":2,\"observedAtLocal\":\"2026-08-15T18:00\"}}");
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

        WeatherCardProjection projection =
            WeatherCardProjection.FromSnapshot(snapshot);

        Assert.IsTrue(projection.HasData);
        Assert.AreEqual("Singapore", projection.LocationLabel);
        StringAssert.Contains(projection.TemperatureText, "31.2");
        StringAssert.Contains(projection.TemperatureText, "°C");
        Assert.AreEqual("Partly cloudy", projection.ConditionText);
        Assert.AreEqual("72%", projection.HumidityText);
        Assert.AreEqual("11.5 km/h", projection.WindText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-074 [WEA-001] imperial unit system converts every shown temperature and speed")]
    public void ImperialUnitSystemConvertsFormattedValues()
    {
        // Numbers on the wire stay metric; only the unitSystem tag changes what is shown.
        using JsonDocument document = JsonDocument.Parse(
            "{\"unitSystem\":\"imperial\",\"location\":{\"label\":\"New York\"}," +
            "\"current\":{\"temperatureC\":20,\"apparentTemperatureC\":25," +
            "\"relativeHumidityPercent\":50,\"windSpeedKmh\":16.09344,\"weatherCode\":0," +
            "\"todayHighTemperatureC\":30,\"todayLowTemperatureC\":0," +
            "\"observedAtLocal\":\"2026-08-31T09:00\"}," +
            "\"hourly\":[{\"timeLocal\":\"2026-08-31T10:00\",\"temperatureC\":10}]," +
            "\"daily\":[{\"dateLocal\":\"2026-09-01\",\"highTemperatureC\":30,\"lowTemperatureC\":0}]}");
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

        WeatherCardProjection projection =
            WeatherCardProjection.FromSnapshot(snapshot);

        Assert.AreEqual("68 °F", projection.TemperatureText);
        Assert.AreEqual("77 °F", projection.ApparentTemperatureText);
        Assert.AreEqual("10 mph", projection.WindText);
        Assert.AreEqual("86 °F", projection.HighTemperatureText);
        Assert.AreEqual("32 °F", projection.LowTemperatureText);
        Assert.AreEqual("50 °F", projection.Hours[0].TemperatureText);
        // The trend geometry still plots the metric number - the curve's shape is unit-free.
        Assert.AreEqual(10d, projection.Hours[0].TemperatureCelsius);
        Assert.AreEqual("86 °F", projection.Days[0].HighTemperatureText);
        Assert.AreEqual("32 °F", projection.Days[0].LowTemperatureText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-008 [CRD-005] missing weather payload fails closed")]
    public void MissingPayloadDoesNotExposeFakeValues()
    {
        var snapshot = new CardRuntimeSnapshot(
            "demo.weather",
            "builtin.weather",
            schemaVersion: 1,
            sequence: 1,
            DateTimeOffset.UtcNow,
            CardRuntimeFreshness.Unknown,
            CardRuntimeStatus.Unavailable);

        WeatherCardProjection projection =
            WeatherCardProjection.FromSnapshot(snapshot);

        Assert.IsFalse(projection.HasData);
        Assert.AreEqual("—", projection.TemperatureText);
        Assert.AreEqual("—", projection.ConditionText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-094 [WEA-001/CRD-001] the card credits the source that produced the reading")]
    public void AttributionFollowsThePayloadSource()
    {
        // Both vendors require attribution, and the card states it in the panel's language,
        // so the projection has to name a string per source. A fixed one would credit
        // whichever vendor happened to be implemented first.
        Assert.AreEqual(
            "WeatherAttributionQWeather",
            ProjectSource("app.winwidgetboard.weather.qweather").AttributionResourceKey);
        Assert.AreEqual(
            "WeatherAttributionOpenMeteo",
            ProjectSource("app.winwidgetboard.weather.open-meteo").AttributionResourceKey);

        // A source this build has never heard of is still credited - in the vendor's own
        // words, which is what the payload carries - rather than mislabelled as a known one.
        WeatherCardProjection unknown = ProjectSource("app.example.weather.other");
        Assert.AreEqual(string.Empty, unknown.AttributionResourceKey);
        Assert.AreEqual("Weather data by Example", unknown.AttributionText);
        Assert.AreEqual("https://example.com/", unknown.AttributionUrl);
    }

    private static WeatherCardProjection ProjectSource(string source)
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"source\":\"" + source + "\"," +
            "\"attribution\":\"Weather data by Example\"," +
            "\"attributionUrl\":\"https://example.com/\"," +
            "\"location\":{\"label\":\"Beijing\"}," +
            "\"current\":{\"temperatureC\":28.2,\"weatherCode\":0}}");
        return WeatherCardProjection.FromSnapshot(
            new CardRuntimeSnapshot(
                "demo.weather",
                "builtin.weather",
                schemaVersion: 1,
                sequence: 1,
                DateTimeOffset.UtcNow,
                CardRuntimeFreshness.Fresh,
                CardRuntimeStatus.Ready,
                document.RootElement,
                [CardRuntimeActionIds.Refresh]));
    }
}
