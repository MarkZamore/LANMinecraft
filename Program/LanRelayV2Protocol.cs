using System.Buffers.Binary;
using System.IO;

namespace Minecraft;

internal enum LanRelayV2FrameType : byte
{
    Data = 1,
    Ack = 2,
    Heartbeat = 3,
    Close = 4
}

internal sealed record LanRelayV2Frame(
    LanRelayV2FrameType Type,
    long Offset,
    byte[] Payload)
{
    public static LanRelayV2Frame Data(long offset, byte[] payload) =>
        new(LanRelayV2FrameType.Data, offset, payload);

    public static LanRelayV2Frame Ack(long offset) =>
        new(LanRelayV2FrameType.Ack, offset, []);

    public static LanRelayV2Frame Heartbeat(long offset) =>
        new(LanRelayV2FrameType.Heartbeat, offset, []);

    public static LanRelayV2Frame Close(long offset, byte[]? payload = null) =>
        new(LanRelayV2FrameType.Close, offset, payload ?? []);
}

internal static class LanRelayV2Protocol
{
    private const int HeaderBytes = 13;

    public const int MaxPayloadBytes = 64 * 1024;
    public const int MaxBufferedBytesPerDirection = 8 * 1024 * 1024;
    public const int MaxBufferedSegmentsPerDirection = 128;
    public const int MaxCloseReasonBytes = 128;
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TransportTimeout = TimeSpan.FromSeconds(6);

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        LanRelayV2Frame frame,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate(frame);

        var header = new byte[HeaderBytes];
        header[0] = (byte)frame.Type;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(1, sizeof(long)), frame.Offset);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(9, sizeof(int)), frame.Payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (frame.Payload.Length > 0)
        {
            await stream.WriteAsync(frame.Payload, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async ValueTask<LanRelayV2Frame> ReadFrameAsync(
        Stream stream,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderBytes];
        await ReadExactAsync(stream, header, token).ConfigureAwait(false);

        var type = (LanRelayV2FrameType)header[0];
        var offset = BinaryPrimitives.ReadInt64BigEndian(
            header.AsSpan(1, sizeof(long)));
        var length = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(9, sizeof(int)));
        ValidateHeader(type, offset, length);

        var payload = length == 0 ? [] : new byte[length];
        if (payload.Length > 0)
        {
            await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
        }

        var frame = new LanRelayV2Frame(type, offset, payload);
        Validate(frame);
        return frame;
    }

    private static void Validate(LanRelayV2Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Payload is null)
        {
            throw new InvalidDataException(
                "Invalid resumable LAN relay frame payload.");
        }
        ValidateHeader(frame.Type, frame.Offset, frame.Payload.Length);
    }

    private static void ValidateHeader(
        LanRelayV2FrameType type,
        long offset,
        int payloadLength)
    {
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException(
                "Invalid resumable LAN relay frame length.");
        }
        if (offset < 0)
        {
            throw new InvalidDataException(
                "Invalid resumable LAN relay frame offset.");
        }
        if (offset > long.MaxValue - payloadLength)
        {
            throw new InvalidDataException(
                "The resumable LAN relay frame range overflows.");
        }

        switch (type)
        {
            case LanRelayV2FrameType.Data when payloadLength == 0:
                throw new InvalidDataException(
                    "A resumable LAN relay data frame cannot be empty.");
            case LanRelayV2FrameType.Ack or LanRelayV2FrameType.Heartbeat
                when payloadLength != 0:
                throw new InvalidDataException(
                    "A resumable LAN relay control frame cannot have a payload.");
            case LanRelayV2FrameType.Close
                when payloadLength > MaxCloseReasonBytes:
                throw new InvalidDataException(
                    "A resumable LAN relay close reason is too large.");
            case LanRelayV2FrameType.Data:
            case LanRelayV2FrameType.Ack:
            case LanRelayV2FrameType.Heartbeat:
            case LanRelayV2FrameType.Close:
                return;
            default:
                throw new InvalidDataException(
                    "Unknown resumable LAN relay frame type.");
        }
    }

    private static async ValueTask ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset),
                token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The resumable LAN relay transport was closed.");
            }
            offset += read;
        }
    }
}
