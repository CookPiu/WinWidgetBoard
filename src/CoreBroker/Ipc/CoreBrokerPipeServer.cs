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
        {
            bool handshakeComplete = false;
            try
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    byte[] frame = await LengthPrefixedFrameCodec
                        .ReadAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);
                    if (!EnvelopeCodec.TryDeserialize(frame, out Envelope? request, out _))
                    {
                        return;
                    }

                    Envelope response;
                    if (!handshakeComplete)
                    {
                        response = HandleHello(request!);
                        handshakeComplete = response.Error is null;
                    }
                    else
                    {
                        response = HandleEstablishedRequest(request!);
                    }

                    if (!EnvelopeCodec.TrySerialize(response, out byte[] responseJson, out _))
                    {
                        return;
                    }

                    await LengthPrefixedFrameCodec
                        .WriteAsync(pipe, responseJson, cancellationToken)
                        .ConfigureAwait(false);

                    if (!handshakeComplete)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ProtocolFrameException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

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
            .ToArray(),
            MaxMessageBytes = ProtocolConstants.MaxMessageBytes,
        };
        return SuccessResponse(request, SessionHelloContract.Method, payload);
    }

    private Envelope HandleEstablishedRequest(Envelope request) =>
        _commandRouter.Handle(request);

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
