using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public interface ILayoutClient
{
    Task<LayoutDto?> GetLayoutAsync(
        string layoutId,
        string displayId,
        CancellationToken cancellationToken);

    Task<LayoutDto> SaveLayoutAsync(
        LayoutSaveRequest request,
        CancellationToken cancellationToken);
}

public sealed class CoreBrokerLayoutClient : ILayoutClient
{
    private readonly CoreBrokerPipeClient _client;

    public CoreBrokerLayoutClient(CoreBrokerPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<LayoutDto?> GetLayoutAsync(
        string layoutId,
        string displayId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                LayoutContract.GetMethod,
                new LayoutGetRequest
                {
                    LayoutId = layoutId,
                    DisplayId = displayId,
                }),
            cancellationToken).ConfigureAwait(false);
        if (response.Error?.Code == "resource.not-found")
        {
            return null;
        }

        EnsureSuccess(response, LayoutContract.GetMethod);
        return Deserialize<LayoutGetResponse>(
            response,
            LayoutContract.GetMethod).Layout;
    }

    public async Task<LayoutDto> SaveLayoutAsync(
        LayoutSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(LayoutContract.SaveMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, LayoutContract.SaveMethod);
        return Deserialize<LayoutSaveResponse>(
            response,
            LayoutContract.SaveMethod).Layout;
    }

    private static Envelope CreateRequest<TPayload>(
        string method,
        TPayload payload) =>
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

    private static TPayload Deserialize<TPayload>(Envelope response, string method)
    {
        try
        {
            return response.Payload.Deserialize<TPayload>(ContractJson.Options)
                ?? throw new CoreBrokerClientException(
                    method,
                    "protocol.empty-payload",
                    "CoreBroker returned an empty response payload.");
        }
        catch (JsonException exception)
        {
            throw new CoreBrokerClientException(
                method,
                "protocol.invalid-payload",
                "CoreBroker returned an invalid response payload.",
                exception);
        }
    }

    private static void EnsureSuccess(Envelope response, string method)
    {
        if (response.Error is null)
        {
            return;
        }

        throw new CoreBrokerClientException(
            method,
            response.Error.Code,
            response.Error.DeveloperMessage ?? response.Error.MessageKey,
            isTransient: response.Error.IsTransient);
    }
}
