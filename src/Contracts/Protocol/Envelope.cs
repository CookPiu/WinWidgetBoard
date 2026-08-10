using System.Text.Json;

namespace WinWidgetBoard.Contracts.Protocol;

public enum EnvelopeMessageType
{
    Request,
    Response,
    Event,
}

public sealed record Envelope
{
    public string ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    public EnvelopeMessageType MessageType { get; init; }

    public Guid MessageId { get; init; }

    public Guid? CorrelationId { get; init; }

    public DateTimeOffset SentAtUtc { get; init; }

    public string Method { get; init; } = string.Empty;

    public JsonElement Payload { get; init; }

    public ContractError? Error { get; init; }
}

public sealed record ContractError
{
    public string Code { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string MessageKey { get; init; } = string.Empty;

    public string? DeveloperMessage { get; init; }

    public Guid? CorrelationId { get; init; }

    public bool IsTransient { get; init; }

    public int? RetryAfterSeconds { get; init; }

    public JsonElement? Details { get; init; }
}

public static class ProtocolConstants
{
    public const int CurrentMajor = 1;
    public const int CurrentMinor = 0;
    public const string CurrentVersion = "1.0";
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxMethodLength = 128;
    public const int MaxErrorCodeLength = 128;
    public const int MaxErrorCategoryLength = 64;
    public const int MaxMessageKeyLength = 128;
    public const int MaxDeveloperMessageLength = 4096;
}
