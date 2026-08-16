using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minecraft;

internal static class PeerSupportProtocol
{
    public const string UpgradeProtocolName = "MinecraftPortableDiagnostics";

    /// <summary>2 = identities are SteamID64; 1 was the Minecraft UUID over TLS.</summary>
    public const int ProtocolVersion = 2;
    public const int MaxFramePayloadBytes = 256 * 1024;
    public const int HeaderSize = 68;
    public const uint MaxLogicalStreamId = 1_000_000;

    private const byte CompressedFlag = 0x01;
    private const int HashOffset = 36;
    private const int HashLength = 32;
    private const int MinimumCompressionSavings = 64;
    private static readonly byte[] FrameMagic = "MPDG"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteUpgradeAsync(Stream stream, CancellationToken token) =>
        PortableProtocol.WriteJsonAsync(
            stream,
            new PeerSupportUpgradeFrame(),
            JsonOptions,
            token);

    public static PeerSupportUpgradeFrame ReadUpgrade(ReadOnlySpan<byte> frame)
    {
        var upgrade = JsonSerializer.Deserialize<PeerSupportUpgradeFrame>(frame, JsonOptions)
            ?? throw new InvalidDataException("The diagnostics upgrade frame is empty.");
        if (!string.Equals(
                upgrade.Protocol,
                UpgradeProtocolName,
                StringComparison.Ordinal) ||
            upgrade.Version != ProtocolVersion)
        {
            throw new InvalidDataException(
                "The peer uses an incompatible diagnostics protocol.");
        }

        return upgrade;
    }

    public static byte[] SerializeJson<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxFramePayloadBytes)
        {
            throw new InvalidDataException("The diagnostics JSON payload is too large.");
        }

        return bytes;
    }

    public static T DeserializeJson<T>(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxFramePayloadBytes)
        {
            throw new InvalidDataException("The diagnostics JSON payload is too large.");
        }

        var value = JsonSerializer.Deserialize<T>(payload.Span, JsonOptions)
            ?? throw new InvalidDataException("The diagnostics JSON payload is empty.");
        switch (value)
        {
            case PeerSupportHello hello:
                ValidateHello(hello);
                break;
            case PeerSupportManifest manifest:
                ValidateManifest(manifest);
                break;
        }

        return value;
    }

    public static void ValidateHello(
        PeerSupportHello hello,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (!string.Equals(
                hello.Protocol,
                UpgradeProtocolName,
                StringComparison.Ordinal) ||
            hello.Version != ProtocolVersion)
        {
            throw new InvalidDataException(
                "The diagnostics hello protocol is incompatible.");
        }

        var referenceTime = now ?? DateTimeOffset.UtcNow;
        if (hello.SessionId == Guid.Empty ||
            hello.StartedAtUtc < referenceTime.AddDays(-7) ||
            hello.StartedAtUtc > referenceTime.AddDays(7) ||
            !TryNormalizeIdentity(hello.SenderIdentityId, out var senderIdentity) ||
            !TryNormalizeIdentity(hello.RecipientIdentityId, out var recipientIdentity) ||
            string.Equals(senderIdentity, recipientIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The diagnostics hello identity is invalid.");
        }
    }

    public static void ValidateManifest(PeerSupportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SessionId == Guid.Empty ||
            manifest.CreatedAtUtc == default ||
            !TryNormalizeIdentity(manifest.SenderIdentityId, out _) ||
            manifest.Streams is null ||
            manifest.Streams.Count > 4096)
        {
            throw new InvalidDataException(
                "The diagnostics manifest identity or size is invalid.");
        }

        var streamIds = new HashSet<uint>();
        foreach (var stream in manifest.Streams)
        {
            if (stream is null ||
                stream.LogicalStreamId == 0 ||
                stream.LogicalStreamId > MaxLogicalStreamId ||
                !Enum.IsDefined(stream.Kind) ||
                !streamIds.Add(stream.LogicalStreamId))
            {
                throw new InvalidDataException(
                    "The diagnostics manifest stream is invalid.");
            }
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        PeerSupportFrame frame,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrameMetadata(frame);

        var decoded = frame.Payload.ToArray();
        if (decoded.Length > MaxFramePayloadBytes)
        {
            throw new InvalidDataException("The diagnostics frame payload is too large.");
        }

        var expectedHash = ComputeFrameHash(
            frame.Type,
            frame.LogicalStreamId,
            frame.Sequence,
            frame.Ack,
            decoded);
        if (!TryParseHash(frame.Sha256, out var declaredHash) ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, declaredHash))
        {
            throw new InvalidDataException("The diagnostics frame payload hash is invalid.");
        }

        var encoded = decoded;
        var flags = (byte)0;
        if (TryCompress(decoded, out var compressed))
        {
            encoded = compressed;
            flags |= CompressedFlag;
        }

        var header = new byte[HeaderSize];
        FrameMagic.CopyTo(header, 0);
        header[4] = ProtocolVersion;
        header[5] = (byte)frame.Type;
        header[6] = flags;
        header[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), frame.LogicalStreamId);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12, 8), frame.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(20, 8), frame.Ack);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28, 4), encoded.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32, 4), decoded.Length);
        expectedHash.CopyTo(header, HashOffset);

        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (encoded.Length > 0)
        {
            await stream.WriteAsync(encoded, token).ConfigureAwait(false);
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<PeerSupportFrame> ReadFrameAsync(
        Stream stream,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, token).ConfigureAwait(false);

        if (!header.AsSpan(0, FrameMagic.Length).SequenceEqual(FrameMagic))
        {
            throw new InvalidDataException("The diagnostics frame magic is invalid.");
        }

        if (header[4] != ProtocolVersion)
        {
            throw new InvalidDataException("The diagnostics frame version is incompatible.");
        }

        var type = (PeerSupportFrameType)header[5];
        if (!Enum.IsDefined(type))
        {
            throw new InvalidDataException("The diagnostics frame type is invalid.");
        }

        var flags = header[6];
        if ((flags & ~CompressedFlag) != 0 || header[7] != 0)
        {
            throw new InvalidDataException("The diagnostics frame flags are invalid.");
        }

        var logicalStreamId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12, 8));
        var ack = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(20, 8));
        var encodedLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(28, 4));
        var decodedLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32, 4));

        if (logicalStreamId > MaxLogicalStreamId)
        {
            throw new InvalidDataException("The diagnostics logical stream ID is invalid.");
        }

        if (sequence == 0 ||
            encodedLength < 0 ||
            encodedLength > MaxFramePayloadBytes ||
            decodedLength < 0 ||
            decodedLength > MaxFramePayloadBytes)
        {
            throw new InvalidDataException("The diagnostics frame dimensions are invalid.");
        }

        var isCompressed = (flags & CompressedFlag) != 0;
        if (!isCompressed && encodedLength != decodedLength)
        {
            throw new InvalidDataException("The diagnostics frame lengths are inconsistent.");
        }

        if (isCompressed && (encodedLength == 0 || decodedLength == 0))
        {
            throw new InvalidDataException("The compressed diagnostics frame is empty.");
        }
        ValidateFrameShape(type, logicalStreamId, decodedLength);

        var encoded = new byte[encodedLength];
        await ReadExactAsync(stream, encoded, token).ConfigureAwait(false);

        byte[] decoded;
        if (isCompressed)
        {
            decoded = new byte[decodedLength];
            if (!BrotliDecoder.TryDecompress(encoded, decoded, out var bytesWritten) ||
                bytesWritten != decodedLength)
            {
                throw new InvalidDataException(
                    "The compressed diagnostics frame is invalid or exceeds its declared size.");
            }
        }
        else
        {
            decoded = encoded;
        }

        var expectedHash = header.AsSpan(HashOffset, HashLength);
        var actualHash = ComputeFrameHash(
            type,
            logicalStreamId,
            sequence,
            ack,
            decoded);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("The diagnostics frame payload hash does not match.");
        }

        return new PeerSupportFrame(
            type,
            logicalStreamId,
            sequence,
            ack,
            decoded);
    }

    public static string GetReceiverFileName(
        PeerSupportLogKind kind,
        uint logicalStreamId)
    {
        if (logicalStreamId == 0 || logicalStreamId > MaxLogicalStreamId)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalStreamId));
        }

        var prefix = kind switch
        {
            PeerSupportLogKind.LauncherLog => "launcher",
            PeerSupportLogKind.GameLog => "game",
            PeerSupportLogKind.CrashReport => "crash",
            PeerSupportLogKind.Environment => "environment",
            PeerSupportLogKind.Events => "events",
            PeerSupportLogKind.Network => "network",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var extension = kind is PeerSupportLogKind.Events or PeerSupportLogKind.Network
            ? ".ndjson"
            : ".log";
        return $"{prefix}-{logicalStreamId:D7}{extension}";
    }

    private static void ValidateFrameMetadata(PeerSupportFrame frame)
    {
        if (!Enum.IsDefined(frame.Type))
        {
            throw new InvalidDataException("The diagnostics frame type is invalid.");
        }

        if (frame.LogicalStreamId > MaxLogicalStreamId)
        {
            throw new InvalidDataException("The diagnostics logical stream ID is invalid.");
        }

        if (frame.Sequence == 0)
        {
            throw new InvalidDataException("The diagnostics frame sequence must be positive.");
        }

        ValidateFrameShape(frame.Type, frame.LogicalStreamId, frame.Payload.Length);
    }

    private static void ValidateFrameShape(
        PeerSupportFrameType type,
        uint logicalStreamId,
        int payloadLength)
    {
        var usesDataStream = type is
            PeerSupportFrameType.Data or
            PeerSupportFrameType.CompleteStream;
        if (usesDataStream == (logicalStreamId == 0))
        {
            throw new InvalidDataException(
                "The diagnostics frame uses an invalid logical stream.");
        }

        if ((type is PeerSupportFrameType.Ack or
             PeerSupportFrameType.CompleteStream or
             PeerSupportFrameType.CompleteSession or
             PeerSupportFrameType.Heartbeat) &&
            payloadLength != 0)
        {
            throw new InvalidDataException(
                "The diagnostics frame type requires an empty payload.");
        }

        if ((type is PeerSupportFrameType.Hello or
             PeerSupportFrameType.HelloAccepted or
             PeerSupportFrameType.Manifest or
             PeerSupportFrameType.Resume or
             PeerSupportFrameType.Data) &&
            payloadLength == 0)
        {
            throw new InvalidDataException(
                "The diagnostics frame type requires a payload.");
        }
    }

    /// <summary>
    /// Identities on the wire are SteamID64s: they authorise the session and
    /// they name a directory under SupportLogs, so nothing else is accepted.
    /// </summary>
    private static bool TryNormalizeIdentity(string? value, out string identity) =>
        SteamId64.TryNormalize(value, out identity);

    private static bool TryCompress(byte[] payload, out byte[] compressed)
    {
        compressed = [];
        if (payload.Length < MinimumCompressionSavings * 2)
        {
            return false;
        }

        var maximum = BrotliEncoder.GetMaxCompressedLength(payload.Length);
        var buffer = new byte[maximum];
        if (!BrotliEncoder.TryCompress(
                payload,
                buffer,
                out var bytesWritten,
                quality: 4,
                window: 22) ||
            bytesWritten + MinimumCompressionSavings >= payload.Length ||
            bytesWritten > MaxFramePayloadBytes)
        {
            return false;
        }

        compressed = buffer.AsSpan(0, bytesWritten).ToArray();
        return true;
    }

    private static bool TryParseHash(string? value, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length != HashLength * 2)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(value);
            return hash.Length == HashLength;
        }
        catch (FormatException)
        {
            hash = [];
            return false;
        }
    }

    internal static byte[] ComputeFrameHash(
        PeerSupportFrameType type,
        uint logicalStreamId,
        ulong sequence,
        ulong ack,
        ReadOnlySpan<byte> payload)
    {
        var metadata = new byte[1 + sizeof(uint) + sizeof(ulong) + sizeof(ulong)];
        metadata[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(1, sizeof(uint)), logicalStreamId);
        BinaryPrimitives.WriteUInt64BigEndian(
            metadata.AsSpan(1 + sizeof(uint), sizeof(ulong)),
            sequence);
        BinaryPrimitives.WriteUInt64BigEndian(
            metadata.AsSpan(1 + sizeof(uint) + sizeof(ulong), sizeof(ulong)),
            ack);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}

internal sealed record PeerSupportUpgradeFrame
{
    [JsonRequired]
    public string Protocol { get; init; } = PeerSupportProtocol.UpgradeProtocolName;

    [JsonRequired]
    public int Version { get; init; } = PeerSupportProtocol.ProtocolVersion;
}

internal sealed record PortableConnectionContext(
    IPAddress RemoteAddress,
    int RemotePort,
    IPAddress LocalAddress,
    int LocalPort,
    string LocalInterfaceId,
    int LocalInterfaceIndex,
    DateTimeOffset AcceptedAtUtc)
{
    public bool IsRemoteLoopback => IPAddress.IsLoopback(RemoteAddress);
}

internal sealed record PeerSupportHello
{
    [JsonRequired]
    public string Protocol { get; init; } = PeerSupportProtocol.UpgradeProtocolName;

    [JsonRequired]
    public int Version { get; init; } = PeerSupportProtocol.ProtocolVersion;

    [JsonRequired]
    public Guid SessionId { get; init; }

    [JsonRequired]
    public string SenderIdentityId { get; init; } = string.Empty;

    [JsonRequired]
    public string RecipientIdentityId { get; init; } = string.Empty;

    [JsonRequired]
    public DateTimeOffset StartedAtUtc { get; init; }

    [JsonRequired]
    public ulong ResumeAfterSequence { get; init; }
}

internal sealed record PeerSupportManifest
{
    [JsonRequired]
    public Guid SessionId { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public string SenderIdentityId { get; init; } = string.Empty;

    public string SenderPlayerName { get; init; } = string.Empty;

    public string LauncherVersion { get; init; } = string.Empty;

    public string GameVersion { get; init; } = string.Empty;

    public string JavaVersion { get; init; } = string.Empty;

    public string PackHash { get; init; } = string.Empty;

    [JsonRequired]
    public IReadOnlyList<PeerSupportManifestStream> Streams { get; init; } =
        Array.Empty<PeerSupportManifestStream>();

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed record PeerSupportManifestStream(
    uint LogicalStreamId,
    PeerSupportLogKind Kind,
    string DisplayName,
    long? SourceLength,
    DateTimeOffset? LastWriteUtc);

internal sealed record PeerSupportFrame
{
    public PeerSupportFrame(
        PeerSupportFrameType type,
        uint logicalStreamId,
        ulong sequence,
        ulong ack,
        ReadOnlyMemory<byte> payload)
    {
        Type = type;
        LogicalStreamId = logicalStreamId;
        Sequence = sequence;
        Ack = ack;
        Payload = payload.ToArray();
        Sha256 = Convert.ToHexString(PeerSupportProtocol.ComputeFrameHash(
            Type,
            LogicalStreamId,
            Sequence,
            Ack,
            Payload.Span));
    }

    public PeerSupportFrameType Type { get; }

    public uint LogicalStreamId { get; }

    public ulong Sequence { get; }

    public ulong Ack { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public string Sha256 { get; }
}

internal enum PeerSupportFrameType : byte
{
    Hello = 1,
    HelloAccepted = 2,
    Manifest = 3,
    Data = 4,
    CompleteStream = 5,
    Ack = 6,
    Resume = 7,
    CompleteSession = 8,
    Cancel = 9,
    Heartbeat = 10,
    Error = 11
}

internal enum PeerSupportLogKind : byte
{
    LauncherLog = 1,
    GameLog = 2,
    CrashReport = 3,
    Environment = 4,
    Events = 5,
    Network = 6
}

internal sealed class PeerSupportReplayGuard
{
    private readonly object _gate = new();
    private ulong _highestAcceptedSequence;

    public PeerSupportReplayGuard(ulong resumeAfterSequence = 0)
    {
        _highestAcceptedSequence = resumeAfterSequence;
    }

    public ulong HighestAcceptedSequence
    {
        get
        {
            lock (_gate)
            {
                return _highestAcceptedSequence;
            }
        }
    }

    public bool TryAccept(ulong sequence)
    {
        if (sequence == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (sequence <= _highestAcceptedSequence)
            {
                return false;
            }

            _highestAcceptedSequence = sequence;
            return true;
        }
    }
}
