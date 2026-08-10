using System.Buffers.Binary;

namespace WinWidgetBoard.Contracts.Protocol;

public sealed class ProtocolFrameException : IOException
{
    public ProtocolFrameException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class LengthPrefixedFrameCodec
{
    public const int HeaderBytes = sizeof(uint);

    public static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidatePayloadLength(payload.Length);

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderBytes];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        ValidatePayloadLength(payloadLength);

        byte[] payload = GC.AllocateUninitializedArray<byte>((int)payloadLength);
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProtocolFrameException(
                "protocol.invalid-frame",
                "The frame ended before the declared payload was fully received.")
            {
                Source = exception.Source,
            };
        }
    }

    private static void ValidatePayloadLength(int payloadLength)
    {
        if (payloadLength <= 0)
        {
            throw new ProtocolFrameException(
                "protocol.invalid-frame",
                "Frame payload length must be greater than zero.");
        }

        if (payloadLength > ProtocolConstants.MaxMessageBytes)
        {
            throw new ProtocolFrameException(
                "protocol.message-too-large",
                "Frame payload exceeds the maximum message size.");
        }
    }

    private static void ValidatePayloadLength(uint payloadLength)
    {
        if (payloadLength == 0)
        {
            throw new ProtocolFrameException(
                "protocol.invalid-frame",
                "Frame payload length must be greater than zero.");
        }

        if (payloadLength > ProtocolConstants.MaxMessageBytes)
        {
            throw new ProtocolFrameException(
                "protocol.message-too-large",
                "Frame payload exceeds the maximum message size.");
        }
    }
}
