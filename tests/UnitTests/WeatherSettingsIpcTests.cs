using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WeatherSettingsIpcTests
{
    [TestMethod(DisplayName = "IT-WEA-001 [WEA-001/SET-001] Weather settings IPC supports get, save, idempotency and conflict")]
    public async Task WeatherSettingsIpcSupportsRevisionedSave()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        await using var scheduler = new ProviderRefreshScheduler();
        await using var host = new ProviderRefreshHost(scheduler);
        using var hub = new CardSnapshotSubscriptionHub();
        using var visibility = new ProviderRefreshVisibilityRegistry(scheduler);
        using var httpClient = new HttpClient(new NoRequestHandler());
        using var runtime = new WeatherProviderRuntime(
            new WeatherSettingsRepository(database),
            host,
            visibility,
            hub,
            httpClient,
            new TimeProviderRefreshClock());
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "weather-settings-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(weatherProviderRuntime: runtime));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var pipeClient = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await pipeClient.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            var client = new CoreBrokerWeatherSettingsClient(pipeClient);
            WeatherSettingsDto initial = await client.GetWeatherSettingsAsync(
                WeatherSettingsContract.DefaultInstanceId,
                CancellationToken.None);
            Assert.AreEqual(WeatherSettingsContract.DefaultLabel, initial.Label);
            Assert.AreEqual(0, initial.Revision);

            var request = new WeatherSettingsSaveRequest
            {
                ClientOperationId = Guid.NewGuid(),
                InstanceId = WeatherSettingsContract.DefaultInstanceId,
                Label = "Tokyo",
                Latitude = 35.6762,
                Longitude = 139.6503,
                ExpectedRevision = initial.Revision,
            };
            WeatherSettingsDto saved = await client.SaveWeatherSettingsAsync(
                request,
                CancellationToken.None);
            Assert.AreEqual(1, saved.Revision);
            Assert.AreEqual("Tokyo", saved.Label);
            Assert.AreEqual(35.6762, saved.Latitude, 0.00001);
            Assert.AreEqual(139.6503, saved.Longitude, 0.00001);

            WeatherSettingsDto duplicate = await client.SaveWeatherSettingsAsync(
                request,
                CancellationToken.None);
            Assert.AreEqual(saved.Revision, duplicate.Revision);
            Assert.AreEqual(saved.UpdatedAtUtc, duplicate.UpdatedAtUtc);

            WeatherSettingsDto loaded = await client.GetWeatherSettingsAsync(
                WeatherSettingsContract.DefaultInstanceId,
                CancellationToken.None);
            Assert.AreEqual("Tokyo", loaded.Label);
            Assert.AreEqual(35.6762, loaded.Latitude, 0.00001);
            Assert.AreEqual(139.6503, loaded.Longitude, 0.00001);

            CoreBrokerClientException conflict = await Assert.ThrowsAsync<CoreBrokerClientException>(
                () => client.SaveWeatherSettingsAsync(
                    request with
                    {
                        ClientOperationId = Guid.NewGuid(),
                        Label = "Sydney",
                        Latitude = -33.8688,
                        Longitude = 151.2093,
                    },
                    CancellationToken.None));
            Assert.AreEqual("conflict.weather-settings-revision", conflict.Code);
            Assert.AreEqual(1, host.RegistrationCount);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-WEA-002 [WEA-001] Weather settings IPC rejects invalid coordinates")]
    public async Task WeatherSettingsIpcRejectsInvalidCoordinates()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        await using var scheduler = new ProviderRefreshScheduler();
        await using var host = new ProviderRefreshHost(scheduler);
        using var hub = new CardSnapshotSubscriptionHub();
        using var visibility = new ProviderRefreshVisibilityRegistry(scheduler);
        using var httpClient = new HttpClient(new NoRequestHandler());
        using var runtime = new WeatherProviderRuntime(
            new WeatherSettingsRepository(database),
            host,
            visibility,
            hub,
            httpClient,
            new TimeProviderRefreshClock());
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "weather-settings-validation-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(weatherProviderRuntime: runtime));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Envelope response = await client.SendAsync(
                CreateRequest(
                    WeatherSettingsContract.SaveMethod,
                    new WeatherSettingsSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        InstanceId = WeatherSettingsContract.DefaultInstanceId,
                        Label = "Invalid",
                        Latitude = 91,
                        Longitude = 0,
                        ExpectedRevision = 0,
                    }),
                CancellationToken.None);
            Assert.IsNotNull(response.Error);
            Assert.AreEqual("validation.invalid-argument", response.Error!.Code);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-WEA-003 [WEA-001/LCH-002] Weather summary IPC reports no reading before the first refresh and validates the instance")]
    public async Task WeatherSummaryIpcReportsNoReadingAndValidatesInstance()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        await using var scheduler = new ProviderRefreshScheduler();
        await using var host = new ProviderRefreshHost(scheduler);
        using var hub = new CardSnapshotSubscriptionHub();
        using var visibility = new ProviderRefreshVisibilityRegistry(scheduler);
        using var httpClient = new HttpClient(new NoRequestHandler());
        using var runtime = new WeatherProviderRuntime(
            new WeatherSettingsRepository(database),
            host,
            visibility,
            hub,
            httpClient,
            new TimeProviderRefreshClock());
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "weather-summary-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(weatherProviderRuntime: runtime));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            SessionHelloResponse helloPayload = hello.Payload.Deserialize<SessionHelloResponse>(
                ContractJson.Options)!;
            CollectionAssert.Contains(
                helloPayload.Capabilities.ToArray(),
                WeatherSettingsContract.SummaryGetMethod);

            // No refresh has produced a payload yet, so the summary must be absent rather
            // than a placeholder the entry could render as a real reading.
            Envelope empty = await client.SendAsync(
                CreateRequest(
                    WeatherSettingsContract.SummaryGetMethod,
                    new WeatherSummaryGetRequest
                    {
                        InstanceId = WeatherSettingsContract.DefaultInstanceId,
                    }),
                CancellationToken.None);
            Assert.IsNull(empty.Error);
            WeatherSummaryGetResponse payload =
                empty.Payload.Deserialize<WeatherSummaryGetResponse>(ContractJson.Options)!;
            Assert.IsNull(payload.Summary);

            Envelope rejected = await client.SendAsync(
                CreateRequest(
                    WeatherSettingsContract.SummaryGetMethod,
                    new WeatherSummaryGetRequest { InstanceId = "other.instance" }),
                CancellationToken.None);
            Assert.IsNotNull(rejected.Error);
            Assert.AreEqual("validation.invalid-argument", rejected.Error!.Code);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-WEA-004 [WEA-001/LCH-002] Weather summary IPC projects the last reading while the panel is hidden")]
    public async Task WeatherSummaryIpcProjectsTheLastHiddenReading()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        await using var scheduler = new ProviderRefreshScheduler();
        await using var host = new ProviderRefreshHost(scheduler);
        using var hub = new CardSnapshotSubscriptionHub();
        using var visibility = new ProviderRefreshVisibilityRegistry(scheduler);
        using var handler = new StubWeatherHandler();
        using var httpClient = new HttpClient(handler);
        using var runtime = new WeatherProviderRuntime(
            new WeatherSettingsRepository(database),
            host,
            visibility,
            hub,
            httpClient,
            new TimeProviderRefreshClock());

        // Program.cs primes these on the real broker; without them the scheduler treats the
        // environment as unknown and runs nothing at all.
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);

        // Nothing reports the panel as visible here, so this exercises the background
        // cadence the taskbar entry depends on.
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        WeatherSummaryDto? captured = null;
        for (int attempt = 0; attempt < 200 && captured is null; attempt++)
        {
            captured = runtime.TryGetSummary();
            if (captured is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.IsNotNull(captured);
        Assert.AreEqual("31", captured!.TemperatureText);
        Assert.AreEqual(WeatherSettingsContract.DefaultLabel, captured.Label);

        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "weather-summary-reading-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(weatherProviderRuntime: runtime));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Envelope response = await client.SendAsync(
                CreateRequest(
                    WeatherSettingsContract.SummaryGetMethod,
                    new WeatherSummaryGetRequest
                    {
                        InstanceId = WeatherSettingsContract.DefaultInstanceId,
                    }),
                CancellationToken.None);
            Assert.IsNull(response.Error);
            WeatherSummaryGetResponse payload =
                response.Payload.Deserialize<WeatherSummaryGetResponse>(ContractJson.Options)!;
            Assert.IsNotNull(payload.Summary);
            Assert.AreEqual("31", payload.Summary!.TemperatureText);
            Assert.AreEqual(
                WeatherSettingsContract.DefaultInstanceId,
                payload.Summary.InstanceId);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    private sealed class StubWeatherHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"timezone\":\"Asia/Singapore\",\"current\":{\"time\":\"2026-08-20T18:00\"," +
                    "\"temperature_2m\":31.2,\"relative_humidity_2m\":72," +
                    "\"apparent_temperature\":36.4,\"weather_code\":2," +
                    "\"wind_speed_10m\":11.5,\"is_day\":1}}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static Envelope CreateRequest(string method, object payload) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = method,
            Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options),
            Error = null,
        };

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }

    private sealed class NoRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The IPC test must not perform HTTP.");
    }
}
