using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public interface ISystemMonitorSettingsClient
{
    Task<SystemMonitorSettingsDto> GetSystemMonitorSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken);

    Task<SystemMonitorSettingsDto> SaveSystemMonitorSettingsAsync(
        SystemMonitorSettingsSaveRequest request,
        CancellationToken cancellationToken);
}

public sealed class CoreBrokerSystemMonitorClient : ISystemMonitorSettingsClient
{
    private readonly CoreBrokerPipeClient _client;

    public CoreBrokerSystemMonitorClient(CoreBrokerPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<SystemMonitorSettingsDto> GetSystemMonitorSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                SystemMonitorContract.SettingsGetMethod,
                new SystemMonitorSettingsGetRequest { InstanceId = instanceId }),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, SystemMonitorContract.SettingsGetMethod);
        return Deserialize<SystemMonitorSettingsGetResponse>(
            response,
            SystemMonitorContract.SettingsGetMethod).Settings;
    }

    public async Task<SystemMonitorSettingsDto> SaveSystemMonitorSettingsAsync(
        SystemMonitorSettingsSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(SystemMonitorContract.SettingsSaveMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, SystemMonitorContract.SettingsSaveMethod);
        return Deserialize<SystemMonitorSettingsSaveResponse>(
            response,
            SystemMonitorContract.SettingsSaveMethod).Settings;
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
