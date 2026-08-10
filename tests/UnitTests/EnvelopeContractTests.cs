using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class EnvelopeContractTests
{
    [TestMethod(DisplayName = "CT-ENVELOPE-001 [G-006] Valid envelope round-trips with stable JSON names")]
    public void ValidEnvelopeRoundTrips()
    {
        Envelope source = CreateRequest();

        bool serialized = EnvelopeCodec.TrySerialize(
            source,
            out byte[] bytes,
            out IReadOnlyList<ContractValidationError> serializeErrors);

        Assert.IsTrue(serialized, string.Join("; ", serializeErrors));
        string json = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(json, "\"protocolVersion\":\"1.0\"");
        StringAssert.Contains(json, "\"messageType\":\"request\"");
        StringAssert.Contains(json, "\"messageId\"");
        StringAssert.Contains(json, "\"correlationId\":null");

        bool deserialized = EnvelopeCodec.TryDeserialize(
            bytes,
            out Envelope? parsed,
            out IReadOnlyList<ContractValidationError> deserializeErrors);

        Assert.IsTrue(deserialized, string.Join("; ", deserializeErrors));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(source.MessageId, parsed!.MessageId);
        Assert.AreEqual(source.Method, parsed.Method);
        Assert.AreEqual(source.Payload.GetProperty("clientType").GetString(), parsed.Payload.GetProperty("clientType").GetString());
    }

    [TestMethod(DisplayName = "CT-ENVELOPE-002 [G-006] Different protocol major is rejected")]
    public void DifferentMajorVersionIsRejected()
    {
        Envelope envelope = CreateRequest() with { ProtocolVersion = "2.0" };

        bool result = EnvelopeCodec.TrySerialize(
            envelope,
            out _,
            out IReadOnlyList<ContractValidationError> errors);

        Assert.IsFalse(result);
        Assert.IsTrue(errors.Any(error => error.Code == "protocol.version-mismatch"));
    }

    [TestMethod(DisplayName = "CT-ENVELOPE-003 [G-006] Response requires correlation id")]
    public void ResponseRequiresCorrelationId()
    {
        Envelope envelope = CreateRequest() with
        {
            MessageType = EnvelopeMessageType.Response,
            Error = null,
        };

        ContractValidationResult result = EnvelopeValidator.Validate(envelope);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Field == "correlationId"));
    }

    [TestMethod(DisplayName = "CT-ENVELOPE-004 [G-006] Oversized payload is rejected before JSON parsing")]
    public void OversizedPayloadIsRejectedBeforeParsing()
    {
        byte[] oversized = new byte[ProtocolConstants.MaxMessageBytes + 1];

        bool result = EnvelopeCodec.TryDeserialize(
            oversized,
            out Envelope? envelope,
            out IReadOnlyList<ContractValidationError> errors);

        Assert.IsFalse(result);
        Assert.IsNull(envelope);
        Assert.IsTrue(errors.Any(error => error.Code == "protocol.message-too-large"));
    }

    [TestMethod(DisplayName = "CT-ENVELOPE-005 [G-006] Malformed JSON and primitive payload are rejected")]
    public void InvalidJsonAndPayloadAreRejected()
    {
        bool malformed = EnvelopeCodec.TryDeserialize(
            Encoding.UTF8.GetBytes("{not-json}"),
            out _,
            out IReadOnlyList<ContractValidationError> malformedErrors);
        Assert.IsFalse(malformed);
        Assert.IsTrue(malformedErrors.Any(error => error.Code == "protocol.invalid-frame"));

        Envelope primitivePayload = CreateRequest() with
        {
            Payload = JsonSerializer.SerializeToElement("not-an-object"),
        };
        ContractValidationResult validation = EnvelopeValidator.Validate(primitivePayload);
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Errors.Any(error => error.Field == "payload"));
    }

    private static Envelope CreateRequest() =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.Parse("5f6060f1-7a4f-482d-81bd-9e9c5f6d130d"),
            CorrelationId = null,
            SentAtUtc = new DateTimeOffset(2026, 8, 7, 8, 0, 0, TimeSpan.Zero),
            Method = "session.hello",
            Payload = JsonSerializer.SerializeToElement(new
            {
                clientType = "workspace-panel",
            }),
            Error = null,
        };
}
