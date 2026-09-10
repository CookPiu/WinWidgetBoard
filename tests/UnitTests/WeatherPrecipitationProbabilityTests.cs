using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The chance of precipitation, which both sources report in their own units and neither
/// reports for every hour. The card draws a band from it, so "no figure" and "no rain" have to
/// stay distinguishable all the way through - a missing hour published as zero would draw a
/// confident flat line saying it will not rain.
/// </summary>
[TestClass]
public sealed class WeatherPrecipitationProbabilityTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly WeatherLocation Location =
        new("Beijing", 39.92, 116.41);

    [TestMethod(DisplayName =
        "UT-WEA-095 [WEA-001/CRD-002] Open-Meteo publishes whole-percent probability, null where absent")]
    public async Task OpenMeteoPublishesProbabilityPerHour()
    {
        using var handler = new StubHandler(_ => JsonResponse(
            """
            {"timezone":"Asia/Shanghai",
             "current":{"time":"2026-08-15T18:00","temperature_2m":31.2,
                        "relative_humidity_2m":72,"apparent_temperature":36.4,
                        "weather_code":2,"wind_speed_10m":11.5,"is_day":1},
             "hourly":{"time":["2026-08-15T18:00","2026-08-15T19:00","2026-08-15T20:00"],
                       "temperature_2m":[31.2,30.1,29.4],
                       "weather_code":[2,2,3],
                       "is_day":[1,1,0],
                       "precipitation_probability":[40,null,7]}}
            """));
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateOpenMeteoRequest(),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, result.Kind);
        JsonElement hourly = result.Payload.GetProperty("hourly");
        Assert.AreEqual(3, hourly.GetArrayLength());
        Assert.AreEqual(
            40,
            hourly[0].GetProperty("precipitationProbabilityPercent").GetInt32());
        // A null in the array is an hour the model had no probability for. It stays null so
        // the card can leave that hour out of the band rather than draw a zero.
        Assert.AreEqual(
            JsonValueKind.Null,
            hourly[1].GetProperty("precipitationProbabilityPercent").ValueKind);
        Assert.AreEqual(
            7,
            hourly[2].GetProperty("precipitationProbabilityPercent").GetInt32());
    }

    [TestMethod(DisplayName =
        "UT-WEA-096 [WEA-001/CRD-002] A missing probability array costs the trend nothing")]
    public async Task OpenMeteoKeepsTheTrendWithoutProbabilities()
    {
        using var handler = new StubHandler(_ => JsonResponse(
            """
            {"timezone":"Asia/Shanghai",
             "current":{"time":"2026-08-15T18:00","temperature_2m":31.2,
                        "relative_humidity_2m":72,"apparent_temperature":36.4,
                        "weather_code":2,"wind_speed_10m":11.5,"is_day":1},
             "hourly":{"time":["2026-08-15T18:00","2026-08-15T19:00"],
                       "temperature_2m":[31.2,30.1],
                       "weather_code":[2,2],
                       "is_day":[1,1]}}
            """));
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateOpenMeteoRequest(),
            CancellationToken.None);

        JsonElement hourly = result.Payload.GetProperty("hourly");
        Assert.AreEqual(2, hourly.GetArrayLength());
        Assert.AreEqual(31.2, hourly[0].GetProperty("temperatureC").GetDouble(), 0.001);
        Assert.AreEqual(
            JsonValueKind.Null,
            hourly[0].GetProperty("precipitationProbabilityPercent").ValueKind);
    }

    [TestMethod(DisplayName =
        "UT-WEA-097 [WEA-001/CRD-002] QWeather's probability fraction becomes whole percent")]
    public void QWeatherScalesTheProbabilityFraction()
    {
        QWeatherApiResponse parsed = QWeatherProvider.ParseResponse(
            Encoding.UTF8.GetBytes(CurrentFixture),
            Encoding.UTF8.GetBytes(
                """
                {"hours":[
                  {"forecastTime":"2026-08-15T19:00+08:00",
                   "condition":{"text":"多云","code":"101"},
                   "temperature":{"value":29.5,"unit":"°C"},
                   "precipitation":{"probability":0.31,"type":"rain"}},
                  {"forecastTime":"2026-08-15T20:00+08:00",
                   "condition":{"text":"小雨","code":"305"},
                   "temperature":{"value":28.1,"unit":"°C"},
                   "precipitation":{"type":"rain"}},
                  {"forecastTime":"2026-08-15T21:00+08:00",
                   "condition":{"text":"小雨","code":"305"},
                   "temperature":{"value":27.4,"unit":"°C"},
                   "precipitation":{"probability":1,"type":"rain"}}
                ]}
                """),
            null,
            InitialUtc);

        Assert.AreEqual(3, parsed.Hourly.Count);
        // The vendor states a fraction of one; the payload's field name promises percent.
        Assert.AreEqual(31, parsed.Hourly[0].PrecipitationProbabilityPercent);
        // No probability on that hour is not a probability of zero.
        Assert.IsNull(parsed.Hourly[1].PrecipitationProbabilityPercent);
        Assert.AreEqual(100, parsed.Hourly[2].PrecipitationProbabilityPercent);
    }

    [TestMethod(DisplayName =
        "UT-WEA-098 [WEA-001/CRD-002] Both sources publish up to five days and twenty-four hours")]
    public void PublishedCountsCoverTheWidestCard()
    {
        // The widest card shows five days beside a full day of hours. The provider publishes
        // the whole set and the card slices it: the broker cannot see a card's size, so a
        // payload cut to one size would starve the other.
        var hours = new List<string>();
        var temperatures = new List<string>();
        var codes = new List<string>();
        var isDay = new List<string>();
        for (int hour = 0; hour < 30; hour++)
        {
            DateTimeOffset time = new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.Zero)
                .AddHours(hour);
            hours.Add("\"" + time.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) + "\"");
            temperatures.Add("20");
            codes.Add("2");
            isDay.Add("1");
        }

        var dates = new List<string>();
        for (int day = 0; day < 8; day++)
        {
            dates.Add(
                "\"" +
                new DateOnly(2026, 8, 15).AddDays(day)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "\"");
        }

        string payload =
            "{\"timezone\":\"Asia/Shanghai\"," +
            "\"current\":{\"time\":\"2026-08-15T18:00\",\"temperature_2m\":31.2," +
            "\"relative_humidity_2m\":72,\"apparent_temperature\":36.4," +
            "\"weather_code\":2,\"wind_speed_10m\":11.5,\"is_day\":1}," +
            "\"hourly\":{\"time\":[" + string.Join(",", hours) + "]," +
            "\"temperature_2m\":[" + string.Join(",", temperatures) + "]," +
            "\"weather_code\":[" + string.Join(",", codes) + "]," +
            "\"is_day\":[" + string.Join(",", isDay) + "]}," +
            "\"daily\":{\"time\":[" + string.Join(",", dates) + "]," +
            "\"weather_code\":[" + string.Join(",", Enumerable.Repeat("2", 8)) + "]," +
            "\"temperature_2m_max\":[" + string.Join(",", Enumerable.Repeat("30", 8)) + "]," +
            "\"temperature_2m_min\":[" + string.Join(",", Enumerable.Repeat("18", 8)) + "]}}";

        using var handler = new StubHandler(_ => JsonResponse(payload));
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);

        ProviderRefreshResult result = provider
            .FetchAsync(CreateOpenMeteoRequest(), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Assert.AreEqual(24, result.Payload.GetProperty("hourly").GetArrayLength());
        Assert.AreEqual(5, result.Payload.GetProperty("daily").GetArrayLength());
    }

    private static ProviderRefreshRequest CreateOpenMeteoRequest() =>
        new(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            OpenMeteoWeatherProvider.CreateRequestKey(Location),
            InitialUtc.AddMinutes(1),
            OpenMeteoWeatherProvider.CreateArguments(Location),
            generation: 1);

    private const string CurrentFixture =
        """
        {"condition":{"text":"少云","code":"102"},
         "temperature":{"value":31.71,"unit":"°C"},
         "feelsLike":{"value":33.64,"unit":"°C"},
         "humidity":0.69,
         "wind":{"speed":{"value":4.74,"unit":"m/s"}}}
        """;

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
