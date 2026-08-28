using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public interface ITokenUsageSettingsClient
{
    /// <summary>
    /// Returns the whole response, not just the stored settings: which vendors this machine
    /// actually has records for rides with it, and asking separately would mean a second round
    /// trip whose answer could already disagree with the first.
    /// </summary>
    Task<TokenUsageSettingsGetResponse> GetTokenUsageSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken);

    Task<TokenUsageSettingsDto> SaveTokenUsageSettingsAsync(
        TokenUsageSettingsSaveRequest request,
        CancellationToken cancellationToken);
}

public sealed class CoreBrokerTokenUsageClient : ITokenUsageSettingsClient
{
    private readonly CoreBrokerPipeClient _client;

    public CoreBrokerTokenUsageClient(CoreBrokerPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<TokenUsageSettingsGetResponse> GetTokenUsageSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                TokenUsageContract.SettingsGetMethod,
                new TokenUsageSettingsGetRequest { InstanceId = instanceId }),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, TokenUsageContract.SettingsGetMethod);
        return Deserialize<TokenUsageSettingsGetResponse>(
            response,
            TokenUsageContract.SettingsGetMethod);
    }

    public async Task<TokenUsageSettingsDto> SaveTokenUsageSettingsAsync(
        TokenUsageSettingsSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(TokenUsageContract.SettingsSaveMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, TokenUsageContract.SettingsSaveMethod);
        return Deserialize<TokenUsageSettingsSaveResponse>(
            response,
            TokenUsageContract.SettingsSaveMethod).Settings;
    }

    private static Envelope CreateRequest<TPayload>(string method, TPayload payload) =>
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
