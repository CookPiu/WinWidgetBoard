using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public interface IWeatherSettingsClient
{
    Task<WeatherSettingsDto> GetWeatherSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken);

    Task<WeatherSettingsDto> SaveWeatherSettingsAsync(
        WeatherSettingsSaveRequest request,
        CancellationToken cancellationToken);
}

public sealed class CoreBrokerWeatherSettingsClient : IWeatherSettingsClient
{
    private readonly CoreBrokerPipeClient _client;

    public CoreBrokerWeatherSettingsClient(CoreBrokerPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<WeatherSettingsDto> GetWeatherSettingsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                WeatherSettingsContract.GetMethod,
                new WeatherSettingsGetRequest { InstanceId = instanceId }),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, WeatherSettingsContract.GetMethod);
        return Deserialize<WeatherSettingsGetResponse>(
            response,
            WeatherSettingsContract.GetMethod).Settings;
    }

    public async Task<WeatherSettingsDto> SaveWeatherSettingsAsync(
        WeatherSettingsSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(WeatherSettingsContract.SaveMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, WeatherSettingsContract.SaveMethod);
        return Deserialize<WeatherSettingsSaveResponse>(
            response,
            WeatherSettingsContract.SaveMethod).Settings;
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
