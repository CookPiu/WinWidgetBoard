using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Commands;

namespace WinWidgetBoard.CoreBroker.Ipc;

public sealed class CoreBrokerPipeServer
{
    private const int RecommendedBufferBytes = 64 * 1024;
    private const int MaxClientVersionLength = 64;

    private readonly string _pipeName;
    private readonly string _sessionToken;
    private readonly string _serverVersion;
    private readonly CoreBrokerCommandRouter _commandRouter;
    private readonly List<Task> _connections = new();

    public CoreBrokerPipeServer(
        string pipeName,
        string sessionToken,
        string serverVersion,
        CoreBrokerCommandRouter? commandRouter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);

        _pipeName = pipeName;
        _sessionToken = sessionToken;
        _serverVersion = serverVersion;
        _commandRouter = commandRouter ?? new CoreBrokerCommandRouter();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = CreatePipe();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    pipe.Dispose();
                    break;
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }

                _connections.RemoveAll(connection => connection.IsCompleted);
                _connections.Add(HandleConnectionAsync(pipe, cancellationToken));
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(_connections).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            RecommendedBufferBytes,
            RecommendedBufferBytes);

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using (pipe)
        using (var connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var writeGate = new SemaphoreSlim(1, 1))
        {
            Guid connectionId = Guid.NewGuid();
            bool handshakeComplete = false;
            CardSnapshotSubscription? subscription = null;
            Task? snapshotWriter = null;
            try
            {
                while (pipe.IsConnected &&
                       !connectionCancellation.IsCancellationRequested)
                {
                    byte[] frame = await LengthPrefixedFrameCodec
                        .ReadAsync(pipe, connectionCancellation.Token)
                        .ConfigureAwait(false);
                    if (!EnvelopeCodec.TryDeserialize(frame, out Envelope? request, out _))
                    {
                        return;
                    }

                    Envelope response;
                    CardSnapshotSubscription? nextSubscription;
                    if (!handshakeComplete)
                    {
                        response = HandleHello(request!);
                        handshakeComplete = response.Error is null;
                        nextSubscription = null;
                    }
                    else
                    {
                        response = HandleEstablishedRequest(
                            request!,
                            connectionId,
                            out nextSubscription);
                    }

                    if (nextSubscription is not null)
                    {
                        nextSubscription.SetOverflowHandler(() =>
                        {
                            Console.Error.WriteLine(
                                $"CoreBroker cards subscription overflow: {nextSubscription.SubscriptionId}");
                            connectionCancellation.Cancel();
                        });
                    }

                    if (!await WriteEnvelopeAsync(
                            pipe,
                            response,
                            writeGate,
                            connectionCancellation.Token)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    if (nextSubscription is not null)
                    {
                        if (snapshotWriter is not null)
                        {
                            await snapshotWriter.ConfigureAwait(false);
                        }

                        subscription = nextSubscription;
                        snapshotWriter = WriteSnapshotEventsAsync(
                            pipe,
                            subscription,
                            writeGate,
                            connectionCancellation);
                    }

                    if (!handshakeComplete)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (
                connectionCancellation.IsCancellationRequested)
            {
            }
            catch (ProtocolFrameException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                connectionCancellation.Cancel();
                subscription?.Dispose();
                if (snapshotWriter is not null)
                {
                    try
                    {
                        await snapshotWriter.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                _commandRouter.RemoveConnection(connectionId);
            }
        }
    }

    private static async Task<bool> WriteEnvelopeAsync(
        NamedPipeServerStream pipe,
        Envelope envelope,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        if (!EnvelopeCodec.TrySerialize(envelope, out byte[] responseJson, out _))
        {
            return false;
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LengthPrefixedFrameCodec
                .WriteAsync(pipe, responseJson, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task WriteSnapshotEventsAsync(
        NamedPipeServerStream pipe,
        CardSnapshotSubscription subscription,
        SemaphoreSlim writeGate,
        CancellationTokenSource connectionCancellation)
    {
        try
        {
            while (!connectionCancellation.IsCancellationRequested)
            {
                IReadOnlyList<CardStateSnapshot> snapshots =
                    await subscription
                        .WaitAndDrainAsync(connectionCancellation.Token)
                        .ConfigureAwait(false);
                foreach (CardStateSnapshot snapshot in snapshots)
                {
                    bool written = await WriteEnvelopeAsync(
                        pipe,
                        CreateSnapshotEvent(snapshot),
                        writeGate,
                        connectionCancellation.Token).ConfigureAwait(false);
                    if (!written)
                    {
                        connectionCancellation.Cancel();
                        return;
                    }
                }
            }
        }
        catch (CardSnapshotSubscriptionOverflowException)
        {
            // A slow client must reconnect and resubscribe rather than receive
            // an incomplete state stream.
            connectionCancellation.Cancel();
        }
        catch (OperationCanceledException) when (
            connectionCancellation.IsCancellationRequested ||
            subscription.IsDisposed)
        {
        }
        catch (IOException)
        {
            connectionCancellation.Cancel();
        }
    }

    private static Envelope CreateSnapshotEvent(CardStateSnapshot snapshot) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Event,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = CardsContract.SnapshotEventMethod,
            Payload = JsonSerializer.SerializeToElement(
                new CardSnapshotEvent { Snapshot = snapshot },
                ContractJson.Options),
            Error = null,
        };

    private Envelope HandleHello(Envelope request)
    {
        if (request.MessageType != EnvelopeMessageType.Request ||
            !string.Equals(request.Method, SessionHelloContract.Method, StringComparison.Ordinal))
        {
            return ErrorResponse(request, "session.unauthorized", "session-unauthorized");
        }

        SessionHelloRequest? hello;
        try
        {
            hello = request.Payload.Deserialize<SessionHelloRequest>(ContractJson.Options);
        }
        catch (JsonException)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (hello is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (!IsValidHello(hello, out string? failureCode, out string? failureCategory))
        {
            return ErrorResponse(
                request,
                failureCode ?? "session.unauthorized",
                failureCategory ?? "session-unauthorized");
        }

        SessionHelloResponse payload = new()
        {
            AcceptedProtocolVersion = ProtocolConstants.CurrentVersion,
            ServerVersion = _serverVersion,
            SessionId = Guid.NewGuid(),
            Capabilities = new[]
            {
                "session.ping",
                PanelVisibilityContract.Method,
            }
            .Concat(_commandRouter.LayoutsAvailable
                ? LayoutContract.Methods
                : Array.Empty<string>())
            .Concat(_commandRouter.NotesAvailable
                ? NotesContract.Methods
                : Array.Empty<string>())
            .Concat(_commandRouter.CardsAvailable
                ? CardsContract.Methods
                : Array.Empty<string>())
            .ToArray(),
            MaxMessageBytes = ProtocolConstants.MaxMessageBytes,
        };
        return SuccessResponse(request, SessionHelloContract.Method, payload);
    }

    private Envelope HandleEstablishedRequest(
        Envelope request,
        Guid connectionId,
        out CardSnapshotSubscription? subscription) =>
        _commandRouter.Handle(request, connectionId, out subscription);

    private bool IsValidHello(
        SessionHelloRequest hello,
        out string? failureCode,
        out string? failureCategory)
    {
        failureCode = null;
        failureCategory = null;
        if (hello.ClientType is not
            (SessionHelloContract.LauncherClientType or
            SessionHelloContract.WorkspacePanelClientType or
            SessionHelloContract.PluginHostClientType) ||
            string.IsNullOrWhiteSpace(hello.ClientVersion) ||
            hello.ClientVersion.Length > MaxClientVersionLength ||
            hello.ProcessId <= 0 ||
            !string.Equals(hello.Architecture, "x64", StringComparison.OrdinalIgnoreCase))
        {
            failureCode = "session.unauthorized";
            failureCategory = "session-unauthorized";
            return false;
        }

        if (!ProtocolVersion.IsSupported(hello.SupportedProtocolRange))
        {
            failureCode = "protocol.version-mismatch";
            failureCategory = "protocol";
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(_sessionToken);
        byte[] actual = Encoding.UTF8.GetBytes(hello.SessionToken);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            failureCode = "session.unauthorized";
            failureCategory = "session-unauthorized";
            return false;
        }

        return true;
    }

    private static Envelope SuccessResponse(
        Envelope request,
        string method,
        object payload) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Response,
            MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = method,
            Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options),
            Error = null,
        };

    private static Envelope ErrorResponse(
        Envelope request,
        string code,
        string category) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Response,
            MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = request.Method,
            Payload = JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
            Error = new ContractError
            {
                Code = code,
                Category = category,
                MessageKey = $"error.{code.Replace('.', '-')}",
                DeveloperMessage = null,
                CorrelationId = request.MessageId,
                IsTransient = false,
                RetryAfterSeconds = null,
                Details = null,
            },
        };
}
