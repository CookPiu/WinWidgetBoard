using System.Text;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// Location search is the first product feature that sends text the user typed to a remote
/// host, so the bounds on what leaves and what comes back are pinned here rather than left
/// to the endpoint's good behaviour.
/// </summary>
[TestClass]
public sealed class WeatherLocationSearchTests
{
    [TestMethod(DisplayName =
        "UT-WEA-011 [WEA-002] A search term is trimmed and length-bounded")]
    public void QueriesAreNormalizedAndBounded()
    {
        Assert.IsTrue(
            WeatherLocationSearchContract.TryNormalizeQuery("  Singapore  ", out string? ok));
        Assert.AreEqual("Singapore", ok);

        Assert.IsFalse(WeatherLocationSearchContract.TryNormalizeQuery(null, out _));
        Assert.IsFalse(WeatherLocationSearchContract.TryNormalizeQuery("   ", out _));
        // One character is not a place name; it is a keystroke, and searching on it would
        // send a request per character typed.
        Assert.IsFalse(WeatherLocationSearchContract.TryNormalizeQuery("a", out _));
        Assert.IsFalse(WeatherLocationSearchContract.TryNormalizeQuery(
            new string('x', WeatherLocationSearchContract.MaxQueryLength + 1),
            out _));
        Assert.IsFalse(WeatherLocationSearchContract.TryNormalizeQuery("Sing\napore", out _));
    }

    [TestMethod(DisplayName =
        "UT-WEA-012 [WEA-002] Geocoding results are parsed and capped")]
    public void ResultsAreParsedAndCapped()
    {
        var payload = new StringBuilder("{\"results\":[");
        for (int index = 0; index < WeatherLocationSearchContract.MaxResults + 4; index++)
        {
            payload.Append(index == 0 ? "" : ",");
            payload.Append(
                "{\"name\":\"Place" + index + "\",\"admin1\":\"Region\"," +
                "\"country\":\"Country\",\"country_code\":\"CC\"," +
                "\"latitude\":1.5,\"longitude\":103.5,\"timezone\":\"Asia/Singapore\"}");
        }
        payload.Append("]}");

        IReadOnlyList<WeatherLocationCandidateDto> results =
            OpenMeteoGeocodingService.Parse(Encoding.UTF8.GetBytes(payload.ToString()));

        Assert.AreEqual(WeatherLocationSearchContract.MaxResults, results.Count);
        Assert.AreEqual("Place0", results[0].Name);
        Assert.AreEqual("Region", results[0].Region);
        Assert.AreEqual("Country", results[0].Country);
        Assert.AreEqual(1.5, results[0].Latitude);
        Assert.AreEqual(103.5, results[0].Longitude);
    }

    [TestMethod(DisplayName =
        "UT-WEA-013 [WEA-002] A missing results array is an empty answer, not a failure")]
    public void MissingResultsArrayIsEmpty()
    {
        // Open-Meteo omits the array entirely when nothing matched.
        Assert.AreEqual(
            0,
            OpenMeteoGeocodingService.Parse(
                Encoding.UTF8.GetBytes("{\"generationtime_ms\":0.1}")).Count);
    }

    [TestMethod(DisplayName =
        "UT-WEA-014 [WEA-002] Rows without usable coordinates are dropped")]
    public void RowsWithoutUsableCoordinatesAreDropped()
    {
        // A half-filled row would look pickable and then resolve to nowhere.
        const string payload = "{\"results\":[" +
            "{\"name\":\"NoCoordinates\"}," +
            "{\"name\":\"OutOfRange\",\"latitude\":120.0,\"longitude\":10.0}," +
            "{\"latitude\":1.0,\"longitude\":2.0}," +
            "{\"name\":\"Good\",\"latitude\":1.0,\"longitude\":2.0}]}";

        IReadOnlyList<WeatherLocationCandidateDto> results =
            OpenMeteoGeocodingService.Parse(Encoding.UTF8.GetBytes(payload));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Good", results[0].Name);
    }

    [TestMethod(DisplayName =
        "UT-WEA-015 [WEA-002] A candidate becomes a label plus a disambiguating line")]
    public void CandidateBecomesLabelAndDescription()
    {
        WeatherLocationOption option = WeatherLocationOption.FromCandidate(
            new WeatherLocationCandidateDto
            {
                Name = "Springfield",
                Region = "Illinois",
                Country = "United States",
                Latitude = 39.8017,
                Longitude = -89.6437,
            });

        Assert.AreEqual("Springfield", option.Label);
        StringAssert.Contains(option.Description, "Illinois");
        StringAssert.Contains(option.Description, "United States");
        Assert.AreEqual("39.8017, -89.6437", option.CoordinatesText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-016 [WEA-002] A city state does not repeat its own name")]
    public void DuplicateRegionAndCountryCollapse()
    {
        WeatherLocationOption option = WeatherLocationOption.FromCandidate(
            new WeatherLocationCandidateDto
            {
                Name = "Singapore",
                Region = "Singapore",
                Country = "Singapore",
                Latitude = 1.3521,
                Longitude = 103.8198,
            });

        Assert.AreEqual("Singapore", option.Description);
    }

    [TestMethod(DisplayName =
        "UT-WEA-017 [WEA-002] An over-long place name is clamped to the label limit")]
    public void OverLongNamesAreClamped()
    {
        WeatherLocationOption option = WeatherLocationOption.FromCandidate(
            new WeatherLocationCandidateDto
            {
                Name = new string('x', WeatherSettingsContract.MaxLabelLength + 20),
                Latitude = 1.0,
                Longitude = 2.0,
            });

        Assert.AreEqual(WeatherSettingsContract.MaxLabelLength, option.Label.Length);
        Assert.IsTrue(
            WeatherSettingsContract.TryNormalizeLabel(option.Label, out _),
            "A clamped label must still be savable.");
    }
}
