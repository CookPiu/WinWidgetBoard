using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CommonWeatherLocationsTests
{
    [TestMethod(DisplayName = "UT-WEATHER-COMMON-001 [SET-002] Every preset saves like a search result")]
    public void EveryPresetSavesLikeASearchResult()
    {
        Assert.IsGreaterThan(0, CommonWeatherLocations.All.Count);

        foreach (WeatherLocationOption option in CommonWeatherLocations.All)
        {
            // A preset that cannot survive the save path is worse than no preset: the user
            // picks it, the dialog fills in, and the commit is rejected for a reason they
            // cannot see or fix.
            Assert.IsTrue(
                WeatherSettingsContract.TryNormalizeLabel(option.Label, out _),
                option.Label);
            Assert.IsTrue(
                WeatherSettingsContract.IsValidCoordinates(
                    option.Latitude,
                    option.Longitude),
                option.Label);
        }
    }

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-002 [SET-002] Presets are distinct places")]
    public void PresetsAreDistinctPlaces()
    {
        // Two rows reading the same is a list that wastes the user's attention, and two rows
        // reading differently at the same point is a list that lies about one of them.
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var points = new HashSet<(double, double)>();
        foreach (WeatherLocationOption option in CommonWeatherLocations.All)
        {
            Assert.IsTrue(labels.Add(option.Label), option.Label);
            Assert.IsTrue(
                points.Add((option.Latitude, option.Longitude)),
                option.Label);
        }
    }

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-003 [SET-002] A place needing no second line reports none")]
    public void APlaceNeedingNoSecondLineReportsNone()
    {
        foreach (WeatherLocationOption option in CommonWeatherLocations.All)
        {
            Assert.AreEqual(
                option.Description.Length > 0,
                option.HasDescription,
                option.Label);
        }

        // Hong Kong, Macau and Taipei carry no second line, so the row must collapse it
        // rather than reserve an empty one.
        Assert.IsTrue(
            CommonWeatherLocations.All.Any(option => !option.HasDescription));
    }
}
