namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// The places the settings dialog offers before the user has searched for anything.
///
/// This is a short hand-written convenience list, not a place database. ADR-0027 rejected
/// adopting one - a licensed dataset such as GeoNames would have to be carried in the repo
/// and attributed, and would still stop at whatever it happens to include. Search remains the
/// mechanism that can reach any place; this list only removes the typing for the handful of
/// cities most users actually pick, and every entry ends up in exactly the same saved shape a
/// search result does.
///
/// Coordinates are city-centre points to four decimal places. Open-Meteo resolves to a grid
/// several kilometres wide, so this is far finer than the forecast it feeds; the dialog also
/// shows them read-only before saving, which is what keeps "what gets sent" visible.
///
/// Names are the Latin forms the geocoder itself returns, so a preset and a search result read
/// the same in the list. The label stays editable afterwards for anyone who wants their card
/// to say something else.
/// </summary>
public static class CommonWeatherLocations
{
    public static IReadOnlyList<WeatherLocationOption> All { get; } =
    [
        Create("Beijing", "Beijing", "China", 39.9042, 116.4074, "Asia/Shanghai"),
        Create("Shanghai", "Shanghai", "China", 31.2304, 121.4737, "Asia/Shanghai"),
        Create("Guangzhou", "Guangdong", "China", 23.1291, 113.2644, "Asia/Shanghai"),
        Create("Shenzhen", "Guangdong", "China", 22.5431, 114.0579, "Asia/Shanghai"),
        Create("Chengdu", "Sichuan", "China", 30.5728, 104.0668, "Asia/Shanghai"),
        Create("Hangzhou", "Zhejiang", "China", 30.2741, 120.1551, "Asia/Shanghai"),
        Create("Wuhan", "Hubei", "China", 30.5928, 114.3055, "Asia/Shanghai"),
        Create("Xi'an", "Shaanxi", "China", 34.3416, 108.9398, "Asia/Shanghai"),
        Create("Nanjing", "Jiangsu", "China", 32.0603, 118.7969, "Asia/Shanghai"),
        Create("Chongqing", "Chongqing", "China", 29.5630, 106.5516, "Asia/Shanghai"),
        Create("Tianjin", "Tianjin", "China", 39.0842, 117.2009, "Asia/Shanghai"),
        Create("Suzhou", "Jiangsu", "China", 31.2989, 120.5853, "Asia/Shanghai"),
        Create("Changsha", "Hunan", "China", 28.2282, 112.9388, "Asia/Shanghai"),
        Create("Qingdao", "Shandong", "China", 36.0671, 120.3826, "Asia/Shanghai"),
        Create("Shenyang", "Liaoning", "China", 41.8057, 123.4315, "Asia/Shanghai"),
        Create("Harbin", "Heilongjiang", "China", 45.8038, 126.5349, "Asia/Shanghai"),
        Create("Zhengzhou", "Henan", "China", 34.7466, 113.6254, "Asia/Shanghai"),
        Create("Kunming", "Yunnan", "China", 25.0389, 102.7183, "Asia/Shanghai"),
        Create("Xiamen", "Fujian", "China", 24.4798, 118.0894, "Asia/Shanghai"),
        // These three are unambiguous on their own, so they carry no second line. Composing
        // one would mean stating an administrative relationship the app has no reason to
        // take a position on, and the name alone already tells the user what they picked.
        Create("Hong Kong", "", "", 22.3193, 114.1694, "Asia/Hong_Kong"),
        Create("Macau", "", "", 22.1987, 113.5439, "Asia/Macau"),
        Create("Taipei", "", "", 25.0330, 121.5654, "Asia/Taipei"),
        Create("Tokyo", "", "Japan", 35.6895, 139.6917, "Asia/Tokyo"),
        Create("Seoul", "", "South Korea", 37.5665, 126.9780, "Asia/Seoul"),
        Create("Singapore", "", "Singapore", 1.3521, 103.8198, "Asia/Singapore"),
        Create("Dubai", "", "United Arab Emirates", 25.2048, 55.2708, "Asia/Dubai"),
        Create("Sydney", "New South Wales", "Australia", -33.8688, 151.2093, "Australia/Sydney"),
        Create("London", "England", "United Kingdom", 51.5074, -0.1278, "Europe/London"),
        Create("Paris", "Île-de-France", "France", 48.8566, 2.3522, "Europe/Paris"),
        Create("Berlin", "Berlin", "Germany", 52.5200, 13.4050, "Europe/Berlin"),
        Create("Moscow", "Moscow", "Russia", 55.7558, 37.6173, "Europe/Moscow"),
        Create("New York", "New York", "United States", 40.7128, -74.0060, "America/New_York"),
        Create("Los Angeles", "California", "United States", 34.0522, -118.2437, "America/Los_Angeles"),
        Create("Toronto", "Ontario", "Canada", 43.6532, -79.3832, "America/Toronto"),
    ];

    private static WeatherLocationOption Create(
        string name,
        string region,
        string country,
        double latitude,
        double longitude,
        string timezone) =>
        WeatherLocationOption.FromCandidate(
            new()
            {
                Name = name,
                Region = region,
                Country = country,
                Latitude = latitude,
                Longitude = longitude,
                Timezone = timezone,
            });
}
