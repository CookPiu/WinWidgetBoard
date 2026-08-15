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
