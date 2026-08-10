namespace WinWidgetBoard.Contracts.Protocol;

public sealed record ContractValidationError(
    string Code,
    string Field,
    string Message);

public sealed record ContractValidationResult(
    IReadOnlyList<ContractValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ContractValidationResult Valid { get; } =
        new(Array.Empty<ContractValidationError>());
}

public static class EnvelopeValidator
{
    public static ContractValidationResult Validate(Envelope? envelope)
    {
        if (envelope is null)
        {
            return new ContractValidationResult(new[]
            {
                Invalid(
                    "validation.invalid-argument",
                    string.Empty,
                    "Envelope is required."),
            });
        }

        var errors = new List<ContractValidationError>();
        ValidateProtocolVersion(envelope.ProtocolVersion, errors);

        if (!Enum.IsDefined(envelope.MessageType))
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "messageType",
                "Message type is not supported."));
        }

        if (envelope.MessageId == Guid.Empty)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "messageId",
                "Message id is required."));
        }

        if (envelope.SentAtUtc == default || envelope.SentAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "sentAtUtc",
                "sentAtUtc must be a non-default UTC timestamp."));
        }

        if (string.IsNullOrWhiteSpace(envelope.Method) ||
            envelope.Method.Length > ProtocolConstants.MaxMethodLength)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "method",
                "Method is required and exceeds the allowed length."));
        }

        if (envelope.Payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "payload",
                "Payload must be a JSON object."));
        }

        if (envelope.MessageType == EnvelopeMessageType.Response &&
            (!envelope.CorrelationId.HasValue || envelope.CorrelationId.Value == Guid.Empty))
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "correlationId",
                "Response correlation id is required."));
        }

        if (envelope.MessageType != EnvelopeMessageType.Response && envelope.Error is not null)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error",
                "Only responses may carry an error."));
        }

        if (envelope.Error is not null)
        {
            ValidateError(envelope.Error, errors);
        }

        return errors.Count == 0
            ? ContractValidationResult.Valid
            : new ContractValidationResult(errors);
    }

    private static void ValidateProtocolVersion(
        string? version,
        List<ContractValidationError> errors)
    {
        if (!ProtocolVersion.TryParse(version, out ProtocolVersion parsed))
        {
            errors.Add(Invalid(
                "protocol.version-mismatch",
                "protocolVersion",
                "Protocol version must use major.minor format."));
            return;
        }

        if (parsed.Major != ProtocolConstants.CurrentMajor)
        {
            errors.Add(Invalid(
                "protocol.version-mismatch",
                "protocolVersion",
                "Protocol major version is not supported."));
        }
    }

    private static void ValidateError(
        ContractError error,
        List<ContractValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(error.Code) ||
            error.Code.Length > ProtocolConstants.MaxErrorCodeLength)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error.code",
                "Error code is required and exceeds the allowed length."));
        }

        if (string.IsNullOrWhiteSpace(error.Category) ||
            error.Category.Length > ProtocolConstants.MaxErrorCategoryLength)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error.category",
                "Error category is required and exceeds the allowed length."));
        }

        if (string.IsNullOrWhiteSpace(error.MessageKey) ||
            error.MessageKey.Length > ProtocolConstants.MaxMessageKeyLength)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error.messageKey",
                "Error message key is required and exceeds the allowed length."));
        }

        if (error.DeveloperMessage?.Length > ProtocolConstants.MaxDeveloperMessageLength)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error.developerMessage",
                "Developer error message exceeds the allowed length."));
        }

        if (error.RetryAfterSeconds is < 0)
        {
            errors.Add(Invalid(
                "validation.invalid-argument",
                "error.retryAfterSeconds",
                "Retry delay cannot be negative."));
        }
    }

    private static ContractValidationError Invalid(
        string code,
        string field,
        string message) =>
        new(code, field, message);
}
