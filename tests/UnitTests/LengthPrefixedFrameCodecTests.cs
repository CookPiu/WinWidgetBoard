using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class LengthPrefixedFrameCodecTests
{
    [TestMethod(DisplayName = "CT-FRAME-001 [G-006] Length-prefixed frame round-trips through partial reads")]
    public async Task FrameRoundTripsThroughPartialReads()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"method\":\"session.hello\"}");
        using var output = new MemoryStream();

        await LengthPrefixedFrameCodec.WriteAsync(output, payload);
        byte[] encoded = output.ToArray();

        Assert.AreEqual(
            (uint)payload.Length,
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(0, LengthPrefixedFrameCodec.HeaderBytes)));

        using var input = new PartialReadStream(encoded, maxChunkSize: 2);
        byte[] decoded = await LengthPrefixedFrameCodec.ReadAsync(input);

        CollectionAssert.AreEqual(payload, decoded);
    }

    [TestMethod(DisplayName = "CT-FRAME-002 [G-006] Zero length frame is rejected")]
    public async Task ZeroLengthFrameIsRejected()
    {
        using var stream = CreateFrame(0, Array.Empty<byte>());

        ProtocolFrameException exception = await CaptureProtocolException(
            async () => await LengthPrefixedFrameCodec.ReadAsync(stream));

        Assert.AreEqual("protocol.invalid-frame", exception.Code);
    }

    [TestMethod(DisplayName = "CT-FRAME-003 [G-006] Oversized frame is rejected before allocation")]
    public async Task OversizedFrameIsRejected()
    {
        using var stream = CreateFrame(
            (uint)ProtocolConstants.MaxMessageBytes + 1,
            Array.Empty<byte>());

        ProtocolFrameException exception = await CaptureProtocolException(
            async () => await LengthPrefixedFrameCodec.ReadAsync(stream));

        Assert.AreEqual("protocol.message-too-large", exception.Code);
    }

    [TestMethod(DisplayName = "CT-FRAME-004 [G-006] Truncated header or payload is rejected")]
    public async Task TruncatedFrameIsRejected()
    {
        using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 1, 2 });

        ProtocolFrameException exception = await CaptureProtocolException(
            async () => await LengthPrefixedFrameCodec.ReadAsync(stream));

        Assert.AreEqual("protocol.invalid-frame", exception.Code);
    }

    [TestMethod(DisplayName = "CT-FRAME-005 [G-006] Oversized write is rejected")]
    public async Task OversizedWriteIsRejected()
    {
        byte[] oversized = new byte[ProtocolConstants.MaxMessageBytes + 1];
        using var stream = new MemoryStream();

        ProtocolFrameException exception = await CaptureProtocolException(
            async () => await LengthPrefixedFrameCodec.WriteAsync(stream, oversized));

        Assert.AreEqual("protocol.message-too-large", exception.Code);
        Assert.AreEqual(0, stream.Length);
    }

    private static MemoryStream CreateFrame(uint length, byte[] payload)
    {
        var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[LengthPrefixedFrameCodec.HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    private static async Task<ProtocolFrameException> CaptureProtocolException(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ProtocolFrameException exception)
        {
            return exception;
        }

        Assert.Fail("Expected ProtocolFrameException.");
        return null!;
    }

    private sealed class PartialReadStream : MemoryStream
    {
        private readonly int _maxChunkSize;

        public PartialReadStream(byte[] buffer, int maxChunkSize)
            : base(buffer, writable: false)
        {
            _maxChunkSize = maxChunkSize;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Memory<byte> chunk = buffer[..Math.Min(buffer.Length, _maxChunkSize)];
            return base.ReadAsync(chunk, cancellationToken);
        }
    }
}
