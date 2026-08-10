using System.IO.Pipes;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public sealed class CoreBrokerPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string? _clientType;
    private string? _sessionToken;
    private bool _handshakeComplete;
    private bool _disposed;

    public CoreBrokerPipeClient(
        string pipeName,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _connectTimeout = ValidateTimeout(connectTimeout ?? TimeSpan.FromSeconds(5), nameof(connectTimeout));
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(5), nameof(requestTimeout));
    }

    public bool IsConnected => _pipe?.IsConnected == true && _handshakeComplete;

    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

    public async Task<Envelope> HandshakeAsync(
        string clientType,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ThrowIfDisposed();

        _clientType = clientType;
        _sessionToken = sessionToken;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectAndHandshakeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Envelope response = await ConnectAndHandshakeCoreAsync(cancellationToken).ConfigureAwait(false);
            EnsureHandshakeSucceeded(response);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<Envelope> SendAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureHandshakeConnection();
            return await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<Envelope> SendWithReconnectAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        (Envelope response, _) = await SendWithReconnectWithStatusAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async Task<(Envelope Response, bool WasReconnected)> SendWithReconnectWithStatusAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasReconnected = false;
            if (!IsConnected)
            {
                Envelope handshake = await ConnectAndHandshakeCoreAsync(cancellationToken).ConfigureAwait(false);
                EnsureHandshakeSucceeded(handshake);
                wasReconnected = true;
            }

            try
            {
                Envelope response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
                return (response, wasReconnected);
            }
            catch (IOException)
            {
                DisconnectCore();
            }
            catch (TimeoutException)
            {
                DisconnectCore();
            }

            Envelope reconnectedHandshake = await ConnectAndHandshakeCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureHandshakeSucceeded(reconnectedHandshake);
            wasReconnected = true;
            Envelope retriedResponse = await SendCoreAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return (retriedResponse, wasReconnected);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<Envelope> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        (Envelope response, _) = await SendHeartbeatWithStatusAsync(
            cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async Task<(Envelope Response, bool WasReconnected)> SendHeartbeatWithStatusAsync(
        CancellationToken cancellationToken)
    {
        (Envelope response, bool wasReconnected) = await SendWithReconnectWithStatusAsync(
            CreatePingRequest(),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessfulResponse(response);
        LastHeartbeatUtc = DateTimeOffset.UtcNow;
        return (response, wasReconnected);
    }

    public async Task<PanelVisibilityReportResponse> ReportPanelVisibilityAsync(
        bool panelVisible,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The client operation ID must not be empty.",
                nameof(clientOperationId));
        }

        Envelope response = await SendWithReconnectAsync(
            CreatePanelVisibilityReportRequest(panelVisible, clientOperationId),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessfulResponse(response);

        try
        {
            PanelVisibilityReportResponse? result = response.Payload.Deserialize<PanelVisibilityReportResponse>(
                ContractJson.Options);
            return result ?? throw new InvalidOperationException(
                "The CoreBroker visibility response payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The CoreBroker visibility response payload was invalid.",
                exception);
        }
    }

    public async Task RunHeartbeatAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            interval,
            TimeSpan.Zero,
            nameof(interval));

        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            try
            {
                await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A broken pipe is transient during a broker restart. The next interval retries.
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                // A request/connect timeout is transient. Keep the heartbeat loop alive.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            DisconnectCore();
            _operationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<Envelope> ConnectAndHandshakeCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_clientType) || string.IsNullOrWhiteSpace(_sessionToken))
        {
            throw new InvalidOperationException("Handshake credentials have not been configured.");
        }

        DisconnectCore();
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            int timeoutMilliseconds = (int)Math.Clamp(
                _connectTimeout.TotalMilliseconds,
                1,
                int.MaxValue);
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            _pipe = pipe;

            Envelope response = await SendCoreAsync(CreateHelloRequest(), cancellationToken)
                .ConfigureAwait(false);
            _handshakeComplete = response.Error is null;
            if (!_handshakeComplete)
            {
                DisconnectCore();
            }

            return response;
        }
        catch
        {
            pipe.Dispose();
            if (ReferenceEquals(_pipe, pipe))
            {
                _pipe = null;
            }

            _handshakeComplete = false;
            throw;
        }
    }

    private async Task<Envelope> SendCoreAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = _pipe ??
            throw new InvalidOperationException("The CoreBroker pipe is not connected.");
        if (!pipe.IsConnected)
        {
            throw new IOException("The CoreBroker pipe is disconnected.");
        }

        if (!EnvelopeCodec.TrySerialize(
                request,
                out byte[] requestJson,
                out IReadOnlyList<ContractValidationError> errors))
        {
            throw new InvalidOperationException(
                $"Invalid IPC request: {string.Join(",", errors.Select(error => error.Code))}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            await LengthPrefixedFrameCodec
                .WriteAsync(pipe, requestJson, timeout.Token)
                .ConfigureAwait(false);
            byte[] responseJson = await LengthPrefixedFrameCodec
                .ReadAsync(pipe, timeout.Token)
                .ConfigureAwait(false);
            if (!EnvelopeCodec.TryDeserialize(
                    responseJson,
                    out Envelope? response,
                    out IReadOnlyList<ContractValidationError> responseErrors))
            {
                throw new InvalidOperationException(
                    $"Invalid IPC response: {string.Join(",", responseErrors.Select(error => error.Code))}");
            }

            return response!;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The CoreBroker request timed out.");
        }
    }

    private Envelope CreateHelloRequest() =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = SessionHelloContract.Method,
            Payload = JsonSerializer.SerializeToElement(
                new SessionHelloRequest
                {
                    ClientType = _clientType!,
                    ClientVersion = "0.1.0",
                    ProcessId = Environment.ProcessId,
                    Architecture = "x64",
                    SessionToken = _sessionToken!,
                    SupportedProtocolRange = new ProtocolVersionRange(),
                },
                ContractJson.Options),
            Error = null,
        };

    private static Envelope CreatePingRequest() =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = "session.ping",
            Payload = JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
            Error = null,
        };

    private static Envelope CreatePanelVisibilityReportRequest(
        bool panelVisible,
        Guid clientOperationId) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = PanelVisibilityContract.Method,
            Payload = JsonSerializer.SerializeToElement(
                new PanelVisibilityReportRequest
                {
                    ClientOperationId = clientOperationId,
                    PanelVisible = panelVisible,
                },
                ContractJson.Options),
            Error = null,
        };

    private void EnsureHandshakeConnection()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The CoreBroker handshake is not complete.");
        }
    }

    private static void EnsureHandshakeSucceeded(Envelope response)
    {
        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"CoreBroker handshake failed: {response.Error.Code}");
        }
    }

    private static void EnsureSuccessfulResponse(Envelope response)
    {
        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"CoreBroker heartbeat failed: {response.Error.Code}");
        }
    }

    private void DisconnectCore()
    {
        _handshakeComplete = false;
        _pipe?.Dispose();
        _pipe = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero,
            parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            timeout.TotalMilliseconds,
            int.MaxValue,
            parameterName);

        return timeout;
    }
}
