using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CommonWeatherLocationsTests
{
    private static IEnumerable<(string Name, IReadOnlyList<WeatherLocationOption> List)> Lists =>
    [
        (nameof(CommonWeatherLocations.ChineseMarket), CommonWeatherLocations.ChineseMarket),
        (nameof(CommonWeatherLocations.International), CommonWeatherLocations.International),
    ];

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-001 [SET-002] Every preset saves like a search result")]
    public void EveryPresetSavesLikeASearchResult()
    {
        foreach ((string name, IReadOnlyList<WeatherLocationOption> list) in Lists)
        {
            Assert.IsGreaterThan(0, list.Count, name);

            foreach (WeatherLocationOption option in list)
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
    }

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-002 [SET-002] Presets are distinct places")]
    public void PresetsAreDistinctPlaces()
    {
        foreach ((string name, IReadOnlyList<WeatherLocationOption> list) in Lists)
        {
            // Two rows reading the same is a list that wastes the user's attention, and two
            // rows reading differently at the same point is a list that lies about one of
            // them. Uniqueness is per list: the two lists never show together.
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var points = new HashSet<(double, double)>();
            foreach (WeatherLocationOption option in list)
            {
                Assert.IsTrue(labels.Add(option.Label), $"{name}: {option.Label}");
                Assert.IsTrue(
                    points.Add((option.Latitude, option.Longitude)),
                    $"{name}: {option.Label}");
            }
        }
    }

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-003 [SET-002] A place needing no second line reports none")]
    public void APlaceNeedingNoSecondLineReportsNone()
    {
        foreach ((string name, IReadOnlyList<WeatherLocationOption> list) in Lists)
        {
            foreach (WeatherLocationOption option in list)
            {
                Assert.AreEqual(
                    option.Description.Length > 0,
                    option.HasDescription,
                    option.Label);
            }

            // Hong Kong (and on the Chinese list also Macau and Taipei) carries no second
            // line, so the row must collapse it rather than reserve an empty one.
            Assert.IsTrue(list.Any(option => !option.HasDescription), name);
        }
    }

    [TestMethod(DisplayName = "UT-WEATHER-COMMON-004 [SET-002] The list follows the UI language")]
    public void TheListFollowsTheUiLanguage()
    {
        // Chinese in any regional flavour gets the mainland-weighted list; everything else -
        // including the null culture a bare host may report - gets the world list.
        Assert.AreSame(
            CommonWeatherLocations.ChineseMarket,
            CommonWeatherLocations.ForCulture(CultureInfo.GetCultureInfo("zh-CN")));
        Assert.AreSame(
            CommonWeatherLocations.ChineseMarket,
            CommonWeatherLocations.ForCulture(CultureInfo.GetCultureInfo("zh-TW")));
        Assert.AreSame(
            CommonWeatherLocations.International,
            CommonWeatherLocations.ForCulture(CultureInfo.GetCultureInfo("en-US")));
        Assert.AreSame(
            CommonWeatherLocations.International,
            CommonWeatherLocations.ForCulture(CultureInfo.GetCultureInfo("de-DE")));
        Assert.AreSame(
            CommonWeatherLocations.International,
            CommonWeatherLocations.ForCulture(null));
    }
}
