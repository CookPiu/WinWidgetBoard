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
}
