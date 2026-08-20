using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The condition token is the only thing three processes agree on about weather appearance:
/// CoreBroker produces it, the panel picks a card illustration from it, and the native
/// taskbar entry picks a glyph from it. These tests pin the mapping so a change to it is a
/// deliberate edit rather than a quiet drift in one consumer.
/// </summary>
[TestClass]
public sealed class WeatherConditionContractTests
{
    [DataRow(0, true, WeatherConditionContract.ClearDay)]
    [DataRow(0, false, WeatherConditionContract.ClearNight)]
    [DataRow(1, true, WeatherConditionContract.PartlyCloudyDay)]
    [DataRow(2, false, WeatherConditionContract.PartlyCloudyNight)]
    [DataRow(3, true, WeatherConditionContract.Cloudy)]
    [DataRow(45, true, WeatherConditionContract.Fog)]
    [DataRow(48, false, WeatherConditionContract.Fog)]
    [DataRow(53, true, WeatherConditionContract.Drizzle)]
    [DataRow(57, true, WeatherConditionContract.Drizzle)]
    [DataRow(65, true, WeatherConditionContract.Rain)]
    [DataRow(82, false, WeatherConditionContract.Rain)]
    [DataRow(75, true, WeatherConditionContract.Snow)]
    [DataRow(86, true, WeatherConditionContract.Snow)]
    [DataRow(95, true, WeatherConditionContract.Thunderstorm)]
    [DataRow(99, false, WeatherConditionContract.Thunderstorm)]
    [TestMethod(DisplayName =
        "UT-WEA-006 [WEA-001] WMO codes map to the drawable condition set")]
    public void WeatherCodesMapToConditions(int code, bool isDay, string expected) =>
        Assert.AreEqual(expected, WeatherConditionContract.FromWeatherCode(code, isDay));

    [TestMethod(DisplayName =
        "UT-WEA-007 [WEA-001] An undocumented WMO code degrades to Unknown")]
    public void UndocumentedCodesDegradeToUnknown()
    {
        // Guessing a nearby condition would draw a confident illustration of weather nobody
        // reported; Unknown draws a neutral mark instead.
        Assert.AreEqual(
            WeatherConditionContract.Unknown,
            WeatherConditionContract.FromWeatherCode(4, isDay: true));
        Assert.AreEqual(
            WeatherConditionContract.Unknown,
            WeatherConditionContract.FromWeatherCode(-1, isDay: false));
        Assert.AreEqual(
            WeatherConditionContract.Unknown,
            WeatherConditionContract.FromWeatherCode(int.MaxValue, isDay: true));
    }

    [TestMethod(DisplayName =
        "UT-WEA-008 [WEA-001] Only day and night vary; the rest ignore daylight")]
    public void OnlyClearAndPartlyCloudyVaryWithDaylight()
    {
        foreach (int code in new[] { 3, 45, 51, 61, 71, 95 })
        {
            Assert.AreEqual(
                WeatherConditionContract.FromWeatherCode(code, isDay: true),
                WeatherConditionContract.FromWeatherCode(code, isDay: false),
                $"WMO {code} must not depend on daylight.");
        }
    }

    [TestMethod(DisplayName =
        "UT-WEA-009 [WEA-001] Every produced token is a known token")]
    public void EveryProducedTokenIsKnown()
    {
        // A token the entry does not recognise would silently fall back to the neutral mark,
        // so the producer must never emit one outside the published set.
        for (int code = -5; code <= 120; code++)
        {
            foreach (bool isDay in new[] { true, false })
            {
                string token = WeatherConditionContract.FromWeatherCode(code, isDay);
                Assert.IsTrue(
                    WeatherConditionContract.IsKnown(token),
                    $"WMO {code} produced an unpublished token '{token}'.");
                Assert.IsTrue(
                    token.Length <= WeatherConditionContract.MaxConditionIconIdLength,
                    $"Token '{token}' exceeds the published length limit.");
            }
        }
    }

    [TestMethod(DisplayName =
        "UT-WEA-010 [WEA-001] Unknown tokens are rejected by IsKnown")]
    public void UnknownTokensAreRejected()
    {
        Assert.IsFalse(WeatherConditionContract.IsKnown(null));
        Assert.IsFalse(WeatherConditionContract.IsKnown(string.Empty));
        Assert.IsFalse(WeatherConditionContract.IsKnown("Rain"));
        Assert.IsFalse(WeatherConditionContract.IsKnown("sunny"));
    }
}
