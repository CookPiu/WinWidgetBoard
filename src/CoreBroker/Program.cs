using System.Security.Cryptography;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Hosting;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.CoreBroker;

internal static class Program
{
    private const int DuplicateInstanceExitCode = 17;
    private const int InvalidArgumentsExitCode = 2;
    private const int ProviderFatalExitCode = 70;

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => string.Equals(
                argument,
                "--pipe-handshake-smoke-test",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await RunPipeHandshakeSmokeAsync().ConfigureAwait(false);
        }

        if (!CoreBrokerDataDirectoryResolver.TryResolve(
                args,
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath(),
                out string? dataDirectory,
                out string? dataDirectoryError))
        {
            Console.Error.WriteLine(
                $"CoreBroker argument validation failed: {dataDirectoryError}");
            return InvalidArgumentsExitCode;
        }

        string instanceMutexName;
        try
        {
            instanceMutexName =
                CoreBrokerInstanceIdentity.ResolveMutexName(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(
                $"CoreBroker argument validation failed: {exception.Message}");
            return InvalidArgumentsExitCode;
        }

        using var instanceLock = new CoreBrokerInstanceLock(instanceMutexName);
        if (!instanceLock.IsAcquired)
        {
            return DuplicateInstanceExitCode;
        }

        string sessionToken = ReadArgument(args, "--session-token") ??
            Environment.GetEnvironmentVariable(
                CoreBrokerPipeNames.SessionTokenEnvironmentVariable) ??
            CreateSessionToken();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var refreshClock = new TimeProviderRefreshClock();
        var fatalSupervisor = new ProviderProcessFatalSupervisor(
            exception =>
            {
                Console.Error.WriteLine(
                    $"CoreBroker provider process-fatal fault: {exception.GetType().Name}");
                cancellation.Cancel();
            });
        await using var providerScheduler = new ProviderRefreshScheduler(
            clock: refreshClock,
            processFatalFaultHandler: fatalSupervisor.Observe);
        await using var providerHost = new ProviderRefreshHost(
            providerScheduler,
            refreshClock);
        providerScheduler.SetNetworkState(ProviderNetworkState.Online);
        providerScheduler.SetPowerState(ProviderPowerState.Normal);
        Task providerHostTask = providerHost.RunAsync(cancellation.Token);

        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(Path.Combine(dataDirectory!, "data.db")),
            cancellation.Token).ConfigureAwait(false);
        await database.ApplySchemaAsync(cancellation.Token).ConfigureAwait(false);

        using var cardSnapshotSubscriptionHub = new CardSnapshotSubscriptionHub();
        using var providerVisibilityRegistry =
            new ProviderRefreshVisibilityRegistry(providerScheduler);
        using var weatherHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var weatherSettingsRepository = new WeatherSettingsRepository(database);
        using var weatherRuntime = new WeatherProviderRuntime(
            weatherSettingsRepository,
            providerHost,
            providerVisibilityRegistry,
            cardSnapshotSubscriptionHub,
            weatherHttpClient,
            refreshClock);
        var server = new CoreBrokerPipeServer(
            CoreBrokerPipeNames.Production,
            sessionToken,
            "0.1.0",
            new CoreBrokerCommandRouter(
                new NoteRepository(database),
                new LayoutRepository(database),
                cardSnapshotSubscriptionHub,
                providerVisibilityRegistry,
                weatherRuntime));
        await server.RunAsync(cancellation.Token).ConfigureAwait(false);
        await providerHostTask.ConfigureAwait(false);
        return fatalSupervisor.HasFatalFault ? ProviderFatalExitCode : 0;
    }

    private static async Task<int> RunPipeHandshakeSmokeAsync()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = CreateSessionToken();
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            var response = await client
                .HandshakeAsync(SessionHelloContract.WorkspacePanelClientType, token, CancellationToken.None)
                .ConfigureAwait(false);
            if (response.Error is not null ||
                response.CorrelationId is null ||
                !response.Payload.TryGetProperty("acceptedProtocolVersion", out _))
            {
                return 1;
            }

            var ping = await client.SendAsync(
                new WinWidgetBoard.Contracts.Protocol.Envelope
                {
                    ProtocolVersion = WinWidgetBoard.Contracts.Protocol.ProtocolConstants.CurrentVersion,
                    MessageType = WinWidgetBoard.Contracts.Protocol.EnvelopeMessageType.Request,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = null,
                    SentAtUtc = DateTimeOffset.UtcNow,
                    Method = "session.ping",
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }, WinWidgetBoard.Contracts.Protocol.ContractJson.Options),
                    Error = null,
                },
                CancellationToken.None).ConfigureAwait(false);
            return ping.Error is null && ping.CorrelationId is not null ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"CoreBroker pipe handshake smoke failed: {exception.GetType().Name}");
            return 1;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string CreateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
