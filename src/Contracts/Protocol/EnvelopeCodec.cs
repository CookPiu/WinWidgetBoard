using System.Text.Json;

namespace WinWidgetBoard.Contracts.Protocol;

public static class EnvelopeCodec
{
    public static bool TrySerialize(
        Envelope envelope,
        out byte[] utf8Json,
        out IReadOnlyList<ContractValidationError> errors)
    {
        utf8Json = Array.Empty<byte>();
        ContractValidationResult validation = EnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            errors = validation.Errors;
            return false;
        }

        try
        {
            utf8Json = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);
        }
        catch (JsonException)
        {
            errors = new[]
            {
                new ContractValidationError(
                    "protocol.invalid-frame",
                    string.Empty,
                    "Envelope could not be serialized."),
            };
            return false;
        }

        if (utf8Json.Length > ProtocolConstants.MaxMessageBytes)
        {
            utf8Json = Array.Empty<byte>();
            errors = new[]
            {
                new ContractValidationError(
                    "protocol.message-too-large",
                    string.Empty,
                    "Envelope exceeds the maximum message size."),
            };
            return false;
        }

        errors = Array.Empty<ContractValidationError>();
        return true;
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        out Envelope? envelope,
        out IReadOnlyList<ContractValidationError> errors)
    {
        envelope = null;
        if (utf8Json.Length == 0)
        {
            errors = new[]
            {
                new ContractValidationError(
                    "protocol.invalid-frame",
                    string.Empty,
                    "Envelope payload cannot be empty."),
            };
            return false;
        }

        if (utf8Json.Length > ProtocolConstants.MaxMessageBytes)
        {
            errors = new[]
            {
                new ContractValidationError(
                    "protocol.message-too-large",
                    string.Empty,
                    "Envelope exceeds the maximum message size."),
            };
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(utf8Json, ContractJson.Options);
        }
        catch (JsonException)
        {
            errors = new[]
            {
                new ContractValidationError(
                    "protocol.invalid-frame",
                    string.Empty,
                    "Envelope payload is not valid UTF-8 JSON."),
            };
            return false;
        }

        ContractValidationResult validation = EnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            envelope = null;
            errors = validation.Errors;
            return false;
        }

        errors = Array.Empty<ContractValidationError>();
        return true;
    }
}
