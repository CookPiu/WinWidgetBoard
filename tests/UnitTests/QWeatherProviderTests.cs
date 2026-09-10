using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The QWeather source. What matters here is not that the vendor answers, but that its answer
/// reaches the card as the exact same payload the first source produces: the panel and the
/// taskbar entry read one set of field names and one condition vocabulary, and every
/// difference between the two vendors has to be absorbed inside the provider.
/// </summary>
[TestClass]
public sealed class QWeatherProviderTests
{
    private const string ApiHost = "abcdefg.qweatherapi.com";
    private const string ApiKey = "TESTKEY1234567890";

    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly WeatherLocation Location =
        new("Beijing", 39.92, 116.41);

    [TestMethod(DisplayName =
        "UT-WEA-077 [WEA-001/CRD-002] QWeather publishes the same payload shape as Open-Meteo")]
    public async Task SuccessProducesTheSharedWeatherPayload()
    {
        using var handler = new StubHandler(RespondWithFixtures);
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(1),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, result.Kind);
        JsonElement current = result.Payload.GetProperty("current");
        Assert.AreEqual(31.71, current.GetProperty("temperatureC").GetDouble(), 0.001);
        Assert.AreEqual(
            33.64,
            current.GetProperty("apparentTemperatureC").GetDouble(),
            0.001);
        // The vendor reports humidity as a fraction; the payload's field name promises a
        // percentage.
        Assert.AreEqual(
            69d,
            current.GetProperty("relativeHumidityPercent").GetDouble(),
            0.001);
        // 4.74 m/s is 17.064 km/h. Publishing the raw value would put a plausible but wrong
        // number under a field name that says km/h.
        Assert.AreEqual(17.064, current.GetProperty("windSpeedKmh").GetDouble(), 0.001);
        // 102 少云 is WMO 1, "mainly clear" - not the vendor's own code. The shared
        // contract draws 1 and 2 with the same partly-cloudy motif, which is what makes the
        // two sources indistinguishable on the card.
        Assert.AreEqual(1, current.GetProperty("weatherCode").GetInt32());
        Assert.AreEqual(
            WeatherConditionContract.PartlyCloudyDay,
            current.GetProperty("conditionIconId").GetString());
        Assert.AreEqual(29.94, current.GetProperty("todayHighTemperatureC").GetDouble(), 0.001);
        Assert.AreEqual(20.93, current.GetProperty("todayLowTemperatureC").GetDouble(), 0.001);
        Assert.AreEqual(
            QWeatherProvider.AttributionText,
            result.Payload.GetProperty("attribution").GetString());
    }

    [TestMethod(DisplayName =
        "UT-WEA-078 [WEA-001/SEC-004] QWeather sends the key as a header, never on the URL")]
    public async Task CredentialTravelsInTheRequestHeaderOnly()
    {
        List<HttpRequestMessage> captured = [];
        using var handler = new StubHandler(request =>
        {
            captured.Add(request);
            return RespondWithFixtures(request);
        });
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        _ = await provider.FetchAsync(CreateRequest(2), CancellationToken.None);

        Assert.AreEqual(3, captured.Count);
        foreach (HttpRequestMessage request in captured)
        {
            Assert.IsFalse(
                request.RequestUri!.AbsoluteUri.Contains(ApiKey, StringComparison.Ordinal),
                "The API key must never appear in a request URI.");
            Assert.IsTrue(request.Headers.TryGetValues("X-QW-Api-Key", out var values));
            Assert.AreEqual(ApiKey, values!.Single());
        }

        // The credential goes on the message, so the shared client - which also carries the
        // keyless Open-Meteo traffic - must not have picked it up.
        Assert.IsFalse(client.DefaultRequestHeaders.Contains("X-QW-Api-Key"));
    }

    [TestMethod(DisplayName =
        "UT-WEA-079 [WEA-001/CRD-002] A gzip-compressed body is decompressed before parsing")]
    public async Task GzipBodiesAreDecompressed()
    {
        using var handler = new StubHandler(request => GzipResponse(FixtureFor(request)));
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(3),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, result.Kind);
        Assert.AreEqual(
            31.71,
            result.Payload.GetProperty("current").GetProperty("temperatureC").GetDouble(),
            0.001);
    }

    [TestMethod(DisplayName =
        "UT-WEA-080 [WEA-001/CRD-002] Forecast timestamps drop their offset so the card reads local time")]
    public async Task ForecastTimestampsArePublishedAsLocalWallClock()
    {
        using var handler = new StubHandler(RespondWithFixtures);
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(4),
            CancellationToken.None);

        JsonElement hourly = result.Payload.GetProperty("hourly");
        Assert.AreEqual(2, hourly.GetArrayLength());
        // 2026-08-15T19:00+08:00 is the local wall clock the card must show. Keeping the
        // offset would make the panel re-render it in the machine's own zone.
        Assert.AreEqual("2026-08-15T19:00", hourly[0].GetProperty("timeLocal").GetString());

        JsonElement daily = result.Payload.GetProperty("daily");
        // Today is skipped: the card already states today's reading above the forecast rows.
        Assert.AreEqual(1, daily.GetArrayLength());
        Assert.AreEqual("2026-08-16", daily[0].GetProperty("dateLocal").GetString());
    }

    [TestMethod(DisplayName =
        "UT-WEA-081 [WEA-001/CRD-002] Day or night comes from the location's own sunrise and sunset")]
    public async Task DaylightIsResolvedFromTheDailyAstroWindow()
    {
        // 22:00 UTC is 06:00 the next day in Beijing - before that day's sunrise, so night.
        var night = new DateTimeOffset(2026, 8, 15, 21, 0, 0, TimeSpan.Zero);
        using var handler = new StubHandler(RespondWithFixtures);
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => night);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(5),
            CancellationToken.None);

        JsonElement current = result.Payload.GetProperty("current");
        Assert.IsFalse(current.GetProperty("isDay").GetBoolean());
        Assert.AreEqual(
            WeatherConditionContract.PartlyCloudyNight,
            current.GetProperty("conditionIconId").GetString());
    }

    [TestMethod(DisplayName =
        "UT-WEA-082 [WEA-001/CRD-002] A failed forecast leaves the current reading intact")]
    public async Task ForecastFailuresDoNotFailTheRefresh()
    {
        using var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("/current/", StringComparison.Ordinal)
                ? JsonResponse(CurrentFixture)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(6),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, result.Kind);
        Assert.AreEqual(0, result.Payload.GetProperty("hourly").GetArrayLength());
        Assert.AreEqual(0, result.Payload.GetProperty("daily").GetArrayLength());
        // With no daily block there is no range to state; the card leaves the line out
        // rather than showing today's reading twice.
        Assert.AreEqual(
            JsonValueKind.Null,
            result.Payload
                .GetProperty("current")
                .GetProperty("todayHighTemperatureC")
                .ValueKind);
    }

    [TestMethod(DisplayName =
        "UT-WEA-083 [WEA-001/SEC-004] A rejected key is reported as needing the user, not as a network fault")]
    public async Task UnauthorizedMapsToPermissionRequired()
    {
        using var handler = new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        var provider = new QWeatherProvider(client, ApiHost, ApiKey, () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(7),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.PermissionRequired, result.Kind);
        Assert.AreEqual("weather.permission-required", result.ErrorCode);
    }

    [TestMethod(DisplayName =
        "UT-WEA-084 [WEA-001/SEC-004] The provider refuses a host outside the vendor's domains")]
    public void ForeignHostsAreRejectedAtConstruction()
    {
        using var client = new HttpClient(new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));

        // A credential is a bearer token: the host it is sent to is part of the security
        // boundary, so a pasted-in third party must not be reachable at all.
        Assert.ThrowsExactly<ArgumentException>(
            () => new QWeatherProvider(client, "collector.example.com", ApiKey));
        Assert.ThrowsExactly<ArgumentException>(
            () => new QWeatherProvider(client, "qweatherapi.com.example.net", ApiKey));
        Assert.ThrowsExactly<ArgumentException>(
            () => new QWeatherProvider(client, ApiHost, string.Empty));
    }

    [TestMethod(DisplayName =
        "UT-WEA-085 [WEA-001/CRD-002] An unselected source reports the missing credential instead of refreshing")]
    public async Task UnconfiguredSourceReportsTheMissingCredential()
    {
        var provider = new UnconfiguredWeatherProvider(
            QWeatherProvider.CredentialsMissingErrorCode,
            () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            new ProviderRefreshRequest(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                UnconfiguredWeatherProvider.CreateRequestKey(Location),
                InitialUtc,
                OpenMeteoWeatherProvider.CreateArguments(Location),
                generation: 1),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.PermissionRequired, result.Kind);
        Assert.AreEqual(QWeatherProvider.CredentialsMissingErrorCode, result.ErrorCode);
        // Nothing here touches the network, so being offline must not hold it back - that is
        // exactly when the user needs to be told why the card is empty.
        Assert.IsFalse(provider.Descriptor.RequiresNetwork);
    }

    [TestMethod(DisplayName =
        "UT-WEA-086 [WEA-001/CRD-002] QWeather condition codes map onto the shared WMO vocabulary")]
    public void ConditionCodesMapToWmoCodes()
    {
        Assert.AreEqual(0, QWeatherConditionCodes.ToWeatherCode("100"));
        Assert.AreEqual(1, QWeatherConditionCodes.ToWeatherCode("102"));
        Assert.AreEqual(2, QWeatherConditionCodes.ToWeatherCode("101"));
        Assert.AreEqual(3, QWeatherConditionCodes.ToWeatherCode("104"));
        Assert.AreEqual(80, QWeatherConditionCodes.ToWeatherCode("300"));
        Assert.AreEqual(95, QWeatherConditionCodes.ToWeatherCode("302"));
        Assert.AreEqual(96, QWeatherConditionCodes.ToWeatherCode("304"));
        Assert.AreEqual(61, QWeatherConditionCodes.ToWeatherCode("305"));
        Assert.AreEqual(66, QWeatherConditionCodes.ToWeatherCode("313"));
        Assert.AreEqual(75, QWeatherConditionCodes.ToWeatherCode("403"));
        // Haze and blowing sand have no WMO interpretation code; fog is the obscuration the
        // card can actually draw, and every one of them is a loss of visibility.
        Assert.AreEqual(45, QWeatherConditionCodes.ToWeatherCode("502"));
        Assert.AreEqual(45, QWeatherConditionCodes.ToWeatherCode("507"));

        // Heat and cold advisories describe no sky condition, and neither does a code this
        // build has never seen. Both become an absent weather code rather than a guess.
        Assert.IsNull(QWeatherConditionCodes.ToWeatherCode("900"));
        Assert.IsNull(QWeatherConditionCodes.ToWeatherCode("999"));
        Assert.IsNull(QWeatherConditionCodes.ToWeatherCode("777"));
        Assert.IsNull(QWeatherConditionCodes.ToWeatherCode("not-a-number"));
        Assert.IsNull(QWeatherConditionCodes.ToWeatherCode(null));

        // Every code the vendor documents must land inside the set the panel can name; an
        // unmapped one would reach the card as "WMO 313" instead of a condition.
        foreach (int wmo in DocumentedConditionCodes
            .Select(QWeatherConditionCodes.ToWeatherCode)
            .Where(code => code is not null)
            .Select(code => code!.Value))
        {
            Assert.AreNotEqual(
                WeatherConditionContract.Unknown,
                WeatherConditionContract.FromWeatherCode(wmo, isDay: true),
                $"WMO {wmo} does not reduce to a drawable condition.");
        }
    }

    [TestMethod(DisplayName =
        "UT-WEA-087 [WEA-002/CRD-002] QWeather geocoding reads string coordinates and its body status")]
    public void GeocodingParsesStringCoordinatesAndStatus()
    {
        IReadOnlyList<WeatherLocationCandidateDto> results = QWeatherGeocodingService.Parse(
            Encoding.UTF8.GetBytes(
                """
                {"code":"200","location":[
                  {"name":"东城","id":"101011600","lat":"39.91755","lon":"116.41876",
                   "adm2":"北京","adm1":"北京市","country":"中国","tz":"Asia/Shanghai"},
                  {"name":"broken","lat":"not-a-number","lon":"116.4"}
                ]}
                """));

        // The second row cannot resolve, so it is skipped rather than offered half-filled.
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("东城", results[0].Name);
        Assert.AreEqual(39.91755, results[0].Latitude, 0.00001);
        Assert.AreEqual(116.41876, results[0].Longitude, 0.00001);
        Assert.AreEqual("北京市", results[0].Region);
        Assert.AreEqual("Asia/Shanghai", results[0].Timezone);

        // GeoAPI answers 200 OK with a status in the body: "404" is an empty answer, and any
        // other non-200 status is a failure the dialog must show as "search unavailable".
        Assert.AreEqual(
            0,
            QWeatherGeocodingService.Parse(Encoding.UTF8.GetBytes("{\"code\":\"404\"}")).Count);
        Assert.ThrowsExactly<InvalidDataException>(
            () => QWeatherGeocodingService.Parse(
                Encoding.UTF8.GetBytes("{\"code\":\"403\"}")));
    }

    /// <summary>The full documented condition set, used to prove the mapping has no holes.</summary>
    private static readonly int[] DocumentedConditionCodeValues =
    [
        100, 101, 102, 103, 104,
        300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313,
        314, 315, 316, 317, 318, 399,
        400, 401, 402, 403, 404, 405, 406, 407, 408, 409, 410, 499,
        500, 501, 502, 503, 504, 507, 508, 509, 510, 511, 512, 513, 514, 515,
    ];

    private static IEnumerable<string> DocumentedConditionCodes =>
        DocumentedConditionCodeValues.Select(
            code => code.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ProviderRefreshRequest CreateRequest(int sequence) =>
        new(
            Guid.Parse($"30000000-0000-0000-0000-00000000000{sequence}"),
            QWeatherProvider.CreateRequestKey(Location),
            InitialUtc.AddMinutes(1),
            QWeatherProvider.CreateArguments(Location),
            generation: sequence);

    private static HttpResponseMessage RespondWithFixtures(HttpRequestMessage request) =>
        JsonResponse(FixtureFor(request));

    private static string FixtureFor(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/hourly/", StringComparison.Ordinal))
        {
            return HourlyFixture;
        }

        return path.Contains("/daily/", StringComparison.Ordinal)
            ? DailyFixture
            : CurrentFixture;
    }

    private const string CurrentFixture =
        """
        {"metadata":{"tag":"t"},
         "condition":{"text":"少云","code":"102"},
         "temperature":{"value":31.71,"unit":"°C"},
         "feelsLike":{"value":33.64,"unit":"°C"},
         "humidity":0.69,
         "wind":{"direction":{"degree":226,"compass":"sw"},
                 "speed":{"value":4.74,"unit":"m/s"},"scale":3},
         "pressure":{"value":1001.5,"unit":"hPa"},
         "cloudCover":0.05,"uvIndex":3}
        """;

    private const string HourlyFixture =
        """
        {"metadata":{"tag":"t"},
         "hours":[
           {"forecastTime":"2026-08-15T19:00+08:00",
            "condition":{"text":"多云","code":"101"},
            "temperature":{"value":29.5,"unit":"°C"}},
           {"forecastTime":"2026-08-15T20:00+08:00",
            "condition":{"text":"小雨","code":"305"},
            "temperature":{"value":28.1,"unit":"°C"}}
         ]}
        """;

    private const string DailyFixture =
        """
        {"metadata":{"tag":"t"},
         "days":[
           {"forecastStartTime":"2026-08-15T00:00+08:00",
            "astro":{"sunrise":"2026-08-15T05:20+08:00","sunset":"2026-08-15T19:10+08:00"},
            "temperatureMax":{"value":29.94,"unit":"°C"},
            "temperatureMin":{"value":20.93,"unit":"°C"},
            "daytime":{"condition":{"text":"小雨","code":"305"}}},
           {"forecastStartTime":"2026-08-16T00:00+08:00",
            "astro":{"sunrise":"2026-08-16T05:21+08:00","sunset":"2026-08-16T19:09+08:00"},
            "temperatureMax":{"value":29.24,"unit":"°C"},
            "temperatureMin":{"value":19.72,"unit":"°C"},
            "daytime":{"condition":{"text":"多云","code":"101"}}}
         ]}
        """;

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage GzipResponse(string content)
    {
        using var raw = new MemoryStream();
        using (var gzip = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(raw.ToArray()),
        };
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

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
