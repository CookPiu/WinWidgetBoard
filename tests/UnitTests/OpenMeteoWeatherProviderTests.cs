using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class OpenMeteoWeatherProviderTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-WEA-005 [WEA-001/LCH-002] Weather keeps an hourly cadence while the panel is hidden")]
    public void HiddenCadenceKeepsAnHourlyBackgroundRefresh()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(httpClient);

        // The taskbar entry renders weather with the panel closed, so hiding must slow the
        // refresh down rather than stop it. See ADR-0024.
        Assert.AreEqual(TimeSpan.FromHours(1), provider.Descriptor.HiddenInterval);
        Assert.AreEqual(TimeSpan.FromMinutes(15), provider.Descriptor.VisibleInterval);
        Assert.IsTrue(provider.Descriptor.HiddenInterval > provider.Descriptor.VisibleInterval);
    }

    [TestMethod(DisplayName =
        "UT-WEA-001 [WEA-001/CRD-002] Open-Meteo request maps current weather payload")]
    public async Task SuccessBuildsMinimalRequestAndPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new StubHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(
                "{\"timezone\":\"Asia/Singapore\",\"current\":{\"time\":\"2026-08-15T18:00\",\"temperature_2m\":31.2,\"relative_humidity_2m\":72,\"apparent_temperature\":36.4,\"weather_code\":2,\"wind_speed_10m\":11.5,\"is_day\":1}}");
        });
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);
        WeatherLocation location = OpenMeteoWeatherProvider.DefaultLocation;
        ProviderRefreshRequest request = CreateRequest(provider, location, 1);

        ProviderRefreshResult result = await provider.FetchAsync(
            request,
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, result.Kind);
        Assert.IsNotNull(capturedRequest);
        StringAssert.Contains(capturedRequest!.RequestUri!.Query, "latitude=1.3521");
        StringAssert.Contains(capturedRequest.RequestUri.Query, "longitude=103.8198");
        StringAssert.Contains(capturedRequest.RequestUri.Query, "weather_code");
        Assert.AreEqual(15, (result.ValidUntilUtc!.Value - InitialUtc).TotalMinutes);
        Assert.AreEqual(
            31.2,
            result.Payload
                .GetProperty("current")
                .GetProperty("temperatureC")
                .GetDouble(),
            0.0001);
        Assert.AreEqual(
            "Weather data by Open-Meteo.com",
            result.Payload.GetProperty("attribution").GetString());
    }

    [TestMethod(DisplayName =
        "UT-WEA-002 [WEA-001/CRD-001/CRD-005] network failure keeps the last successful weather payload")]
    public async Task NetworkFailureUsesLocationScopedMemoryCache()
    {
        int callCount = 0;
        using var handler = new StubHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return JsonResponse(
                    "{\"timezone\":\"Asia/Singapore\",\"current\":{\"time\":\"2026-08-15T18:00\",\"temperature_2m\":31.2,\"relative_humidity_2m\":72,\"apparent_temperature\":36.4,\"weather_code\":2,\"wind_speed_10m\":11.5,\"is_day\":1}}");
            }

            throw new HttpRequestException("offline");
        });
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc.AddMinutes(20));
        WeatherLocation location = OpenMeteoWeatherProvider.DefaultLocation;

        ProviderRefreshResult first = await provider.FetchAsync(
            CreateRequest(provider, location, 1),
            CancellationToken.None);
        ProviderRefreshResult second = await provider.FetchAsync(
            CreateRequest(provider, location, 2),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Success, first.Kind);
        Assert.AreEqual(ProviderRefreshResultKind.Offline, second.Kind);
        Assert.AreEqual(
            first.Payload.GetProperty("current").GetProperty("temperatureC").GetDouble(),
            second.Payload.GetProperty("current").GetProperty("temperatureC").GetDouble(),
            0.0001);
        Assert.AreEqual(first.ProducedAtUtc, second.ProducedAtUtc);
        Assert.AreEqual("weather.network", second.ErrorCode);
    }

    [TestMethod(DisplayName =
        "UT-WEA-003 [WEA-001/CRD-005] invalid response is bounded as provider failure")]
    public async Task OversizedResponseIsRejected()
    {
        using var handler = new StubHandler(_ =>
            JsonResponse(new string('x', OpenMeteoWeatherProvider.MaxResponseBytes + 1)));
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(
                provider,
                OpenMeteoWeatherProvider.DefaultLocation,
                1),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.Failed, result.Kind);
        Assert.AreEqual("weather.invalid-response", result.ErrorCode);
    }

    [TestMethod(DisplayName =
        "UT-WEA-004 [WEA-001/NFR-REL-004] Retry-After is preserved for rate limiting")]
    public async Task RateLimitPreservesRetryAfter()
    {
        using var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(
                TimeSpan.FromSeconds(9));
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new OpenMeteoWeatherProvider(
            client,
            new Uri("https://weather.test/v1/forecast"),
            () => InitialUtc);

        ProviderRefreshResult result = await provider.FetchAsync(
            CreateRequest(
                provider,
                OpenMeteoWeatherProvider.DefaultLocation,
                1),
            CancellationToken.None);

        Assert.AreEqual(ProviderRefreshResultKind.RateLimited, result.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(9), result.RetryAfter);
    }

    private static ProviderRefreshRequest CreateRequest(
        OpenMeteoWeatherProvider provider,
        WeatherLocation location,
        int sequence)
    {
        return new ProviderRefreshRequest(
            Guid.Parse($"10000000-0000-0000-0000-00000000000{sequence}"),
            OpenMeteoWeatherProvider.CreateRequestKey(location),
            InitialUtc.AddMinutes(1),
            OpenMeteoWeatherProvider.CreateArguments(location),
            generation: sequence);
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                System.Text.Encoding.UTF8,
                "application/json"),
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
