using System.Diagnostics;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;

namespace WinWidgetBoard.WorkspacePanel.Ipc;

public sealed class CoreBrokerSession : IAsyncDisposable
{
    public const string SessionTokenEnvironmentVariable =
        CoreBrokerPipeNames.SessionTokenEnvironmentVariable;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] StartRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
    ];

    private readonly CoreBrokerPipeClient _client;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private Task<bool>? _startTask;
    private bool _disposed;
    private readonly TimeSpan _heartbeatInterval;
    private bool _desiredPanelVisible;
    private int _hasReportedVisibility;

    public CoreBrokerSession(
        CoreBrokerPipeClient? client = null,
        TimeSpan? heartbeatInterval = null)
    {
        _client = client ?? new CoreBrokerPipeClient(
            CoreBrokerPipeNames.Production,
            ConnectTimeout,
            RequestTimeout);
        Notes = new CoreBrokerNotesClient(_client);
        Layout = new CoreBrokerLayoutClient(_client);
        Cards = new CoreBrokerCardsClient(_client);
        WeatherSettings = new CoreBrokerWeatherSettingsClient(_client);
        SystemMonitor = new CoreBrokerSystemMonitorClient(_client);
        TokenUsage = new CoreBrokerTokenUsageClient(_client);
        _heartbeatInterval = heartbeatInterval ?? HeartbeatInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _heartbeatInterval,
            TimeSpan.Zero,
            nameof(heartbeatInterval));
    }

    public bool IsConnected => _client.IsConnected;

    public DateTimeOffset? LastHeartbeatUtc => _client.LastHeartbeatUtc;

    public CoreBrokerNotesClient Notes { get; }

    public CoreBrokerLayoutClient Layout { get; }

    public CoreBrokerCardsClient Cards { get; }

    public CoreBrokerWeatherSettingsClient WeatherSettings { get; }

    public CoreBrokerSystemMonitorClient SystemMonitor { get; }

    public CoreBrokerTokenUsageClient TokenUsage { get; }

    public event EventHandler? Reconnected;

    public async Task<bool> ReportVisibilityAsync(
        bool panelVisible,
        CancellationToken cancellationToken)
    {
        _desiredPanelVisible = panelVisible;
        try
        {
            await _client.ReportPanelVisibilityAsync(
                panelVisible,
                Guid.NewGuid(),
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _hasReportedVisibility, 1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Task<bool> TryStartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startTask is not null)
        {
            return _startTask;
        }

        _startTask = TryStartCoreAsync(cancellationToken);
        return _startTask;
    }

    private async Task<bool> TryStartCoreAsync(CancellationToken cancellationToken)
    {
        string? sessionToken = Environment.GetEnvironmentVariable(
            SessionTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        for (int attempt = 0; attempt <= StartRetryDelays.Length; attempt++)
        {
            try
            {
                Envelope response = await _client.HandshakeAsync(
                    SessionHelloContract.WorkspacePanelClientType,
                    sessionToken,
                    cancellationToken).ConfigureAwait(false);
                if (response.Error is not null)
                {
                    WriteDiagnostic($"CoreBroker handshake rejected: {response.Error.Code}");
                    return false;
                }

                _heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                _heartbeatTask = RunHeartbeatAsync(_heartbeatCancellation.Token);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException) when (attempt < StartRetryDelays.Length)
            {
                await DelayBeforeStartRetryAsync(
                    attempt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (attempt < StartRetryDelays.Length)
            {
                await DelayBeforeStartRetryAsync(
                    attempt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                WriteDiagnostic("CoreBroker is unavailable during handshake");
                return false;
            }
            catch (TimeoutException)
            {
                WriteDiagnostic("CoreBroker handshake timed out");
                return false;
            }
            catch (InvalidOperationException exception)
            {
                WriteDiagnostic($"CoreBroker session was not established: {exception.Message}");
                return false;
            }
        }

        return false;
    }

    private static async Task DelayBeforeStartRetryAsync(
        int attempt,
        CancellationToken cancellationToken)
    {
        WriteDiagnostic($"CoreBroker handshake unavailable; retry {attempt + 1}/{StartRetryDelays.Length}");
        await Task.Delay(StartRetryDelays[attempt], cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeatCancellation?.Cancel();
        if (_startTask is not null)
        {
            try
            {
                await _startTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _heartbeatCancellation?.Dispose();
        Cards.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                (_, bool wasReconnected) = await _client
                    .SendHeartbeatWithStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                bool visibilityReported = true;
                if (wasReconnected && Volatile.Read(ref _hasReportedVisibility) != 0)
                {
                    visibilityReported = await ReportVisibilityAsync(
                        _desiredPanelVisible,
                        cancellationToken).ConfigureAwait(false);
                }

                if (wasReconnected && visibilityReported)
                {
                    NotifyReconnected();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                WriteDiagnostic("CoreBroker heartbeat stopped after a pipe failure");
            }
            catch (TimeoutException)
            {
                WriteDiagnostic("CoreBroker heartbeat stopped after a timeout");
            }
            catch (InvalidOperationException exception)
            {
                WriteDiagnostic($"CoreBroker heartbeat stopped: {exception.Message}");
            }
        }
    }

    private static void WriteDiagnostic(string message)
    {
        Debug.WriteLine(message);
        Console.Error.WriteLine(message);
    }

    private void NotifyReconnected()
    {
        try
        {
            Reconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException &&
                exception is not StackOverflowException &&
                exception is not AccessViolationException)
        {
            WriteDiagnostic($"CoreBroker reconnect callback failed: {exception.Message}");
        }
    }
}
