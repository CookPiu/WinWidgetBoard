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
    private readonly object _connectionGate = new();
    private ConnectionState? _connection;
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

    public bool IsConnected
    {
        get
        {
            lock (_connectionGate)
            {
                return _handshakeComplete && _connection?.Pipe.IsConnected == true;
            }
        }
    }

    public event EventHandler<CoreBrokerEventReceivedEventArgs>? EventReceived;

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
        ConnectionState? state = null;
        try
        {
            int timeoutMilliseconds = (int)Math.Clamp(
                _connectTimeout.TotalMilliseconds,
                1,
                int.MaxValue);
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            state = new ConnectionState(pipe);
            lock (_connectionGate)
            {
                _connection = state;
                _handshakeComplete = false;
            }

            Envelope response = await SendHandshakeCoreAsync(
                    state,
                    CreateHelloRequest(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.Error is not null)
            {
                DisconnectCore();
            }
            else
            {
                lock (_connectionGate)
                {
                    if (ReferenceEquals(_connection, state))
                    {
                        _handshakeComplete = true;
                        state.ReaderTask = ReaderLoopAsync(state);
                    }
                }
            }

            return response;
        }
        catch
        {
            if (state is not null)
            {
                DisconnectCore();
            }
            else
            {
                pipe.Dispose();
            }

            throw;
        }
    }

    private async Task<Envelope> SendHandshakeCoreAsync(
        ConnectionState state,
        Envelope request,
        CancellationToken cancellationToken)
    {
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
                .WriteAsync(state.Pipe, requestJson, timeout.Token)
                .ConfigureAwait(false);
            byte[] responseJson = await LengthPrefixedFrameCodec
                .ReadAsync(state.Pipe, timeout.Token)
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
            throw new TimeoutException("The CoreBroker handshake timed out.");
        }
    }

    private async Task<Envelope> SendCoreAsync(
        Envelope request,
        CancellationToken cancellationToken)
    {
        ConnectionState state = _connection ??
            throw new InvalidOperationException("The CoreBroker pipe is not connected.");
        if (!state.Pipe.IsConnected)
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
        var pendingResponse = new TaskCompletionSource<Envelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (state.Gate)
        {
            if (state.PendingResponse is not null)
            {
                throw new InvalidOperationException(
                    "Only one CoreBroker request may be active on a pipe connection.");
            }

            state.PendingRequestId = request.MessageId;
            state.PendingResponse = pendingResponse;
        }

        try
        {
            await LengthPrefixedFrameCodec
                .WriteAsync(state.Pipe, requestJson, timeout.Token)
                .ConfigureAwait(false);
            return await pendingResponse.Task
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The CoreBroker request timed out.");
        }
        finally
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.PendingResponse, pendingResponse))
                {
                    state.PendingResponse = null;
                }
            }
        }
    }

    private async Task ReaderLoopAsync(ConnectionState state)
    {
        Exception? terminalException = null;
        try
        {
            while (state.Pipe.IsConnected &&
                   !state.Cancellation.IsCancellationRequested)
            {
                byte[] frame = await LengthPrefixedFrameCodec
                    .ReadAsync(state.Pipe, state.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!EnvelopeCodec.TryDeserialize(
                        frame,
                        out Envelope? message,
                        out IReadOnlyList<ContractValidationError> errors) ||
                    message is null)
                {
                    terminalException = new InvalidOperationException(
                        $"Invalid IPC message: {string.Join(",", errors.Select(error => error.Code))}");
                    break;
                }

                if (message.MessageType == EnvelopeMessageType.Event)
                {
                    PublishEvent(message);
                    continue;
                }

                if (message.MessageType != EnvelopeMessageType.Response)
                {
                    terminalException = new InvalidOperationException(
                        "The CoreBroker sent a non-response message for a request.");
                    break;
                }

                TaskCompletionSource<Envelope>? pendingResponse;
                lock (state.Gate)
                {
                    pendingResponse = state.PendingResponse;
                    if (pendingResponse is null ||
                        message.CorrelationId != state.PendingRequestId)
                    {
                        terminalException = new InvalidOperationException(
                            "The CoreBroker response correlation ID did not match the active request.");
                    }
                }

                if (terminalException is not null)
                {
                    break;
                }

                pendingResponse!.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) when (
            state.Cancellation.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            terminalException = exception;
        }
        catch (ObjectDisposedException) when (state.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            TaskCompletionSource<Envelope>? pendingResponse;
            lock (state.Gate)
            {
                pendingResponse = state.PendingResponse;
                state.PendingResponse = null;
            }

            if (pendingResponse is not null && terminalException is not null)
            {
                pendingResponse.TrySetException(terminalException);
            }

            lock (_connectionGate)
            {
                if (ReferenceEquals(_connection, state))
                {
                    _handshakeComplete = false;
                }
            }

        }
    }

    private void PublishEvent(Envelope message)
    {
        EventHandler<CoreBrokerEventReceivedEventArgs>? handler = EventReceived;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(
                this,
                new CoreBrokerEventReceivedEventArgs(message));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException &&
            exception is not AccessViolationException)
        {
            // A consumer callback must not tear down the IPC reader.
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
        ConnectionState? state;
        lock (_connectionGate)
        {
            state = _connection;
            _connection = null;
            _handshakeComplete = false;
        }

        if (state is null)
        {
            return;
        }

        state.Cancellation.Cancel();
        TaskCompletionSource<Envelope>? pendingResponse;
        lock (state.Gate)
        {
            pendingResponse = state.PendingResponse;
            state.PendingResponse = null;
        }

        pendingResponse?.TrySetException(
            new IOException("The CoreBroker pipe connection was closed."));
        state.Pipe.Dispose();
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

    private sealed class ConnectionState
    {
        public ConnectionState(NamedPipeClientStream pipe)
        {
            Pipe = pipe;
        }

        public object Gate { get; } = new();

        public NamedPipeClientStream Pipe { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? ReaderTask { get; set; }

        public Guid PendingRequestId { get; set; }

        public TaskCompletionSource<Envelope>? PendingResponse { get; set; }
    }
}

public sealed class CoreBrokerEventReceivedEventArgs : EventArgs
{
    public CoreBrokerEventReceivedEventArgs(Envelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }

    public Envelope Message { get; }
}
