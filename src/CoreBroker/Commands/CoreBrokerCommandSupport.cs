using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Commands;

internal static class CoreBrokerCommandSupport
{
    public static bool TryDeserializePayload<T>(
        JsonElement payload,
        out T? value)
        where T : class
    {
        value = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            value = payload.Deserialize<T>(ContractJson.Options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsValidOperationId(Guid value) => value != Guid.Empty;

    public static bool IsValidRequiredText(string? value, int maxLength) =>
        IsValidText(value, maxLength) && !string.IsNullOrWhiteSpace(value);

    public static bool IsValidText(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength;

    public static Envelope SuccessResponse(
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

    public static Envelope ErrorResponse(
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
