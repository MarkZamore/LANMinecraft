using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PeerSupportProtocolTests
{
    [Fact]
    public void EphemeralCertificate_IsSelfSignedP256AndUsableByBothTlsRoles()
    {
        using var owner = PeerSupportCertificate.CreateEphemeral(
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var certificate = owner.Certificate;

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.Equal("1.2.840.10045.2.1", certificate.PublicKey.Oid.Value);
        Assert.Equal(
            "1.2.840.10045.4.3.2",
            certificate.SignatureAlgorithm.Value);

        using var publicKey = certificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.Equal(256, publicKey.KeySize);

        var constraints = Assert.Single(
            certificate.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.False(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);

        var keyUsage = Assert.Single(
            certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);

        var enhancedUsage = Assert.Single(
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Contains(
            enhancedUsage.EnhancedKeyUsages.Cast<Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        Assert.Contains(
            enhancedUsage.EnhancedKeyUsages.Cast<Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.2");
    }

    [Fact]
    public void Fingerprint_NormalizesSeparatorsAndMatchesOnlyExactCertificate()
    {
        using var expected = PeerSupportCertificate.CreateEphemeral();
        using var different = PeerSupportCertificate.CreateEphemeral();
        var separated = string.Join(
            ":",
            Enumerable.Range(0, expected.Fingerprint.Length / 2)
                .Select(index => expected.Fingerprint.Substring(index * 2, 2)))
            .ToLowerInvariant();

        Assert.True(PeerSupportCertificate.TryNormalizeFingerprint(
            $"  {separated}  ",
            out var normalized));
        Assert.Equal(expected.Fingerprint, normalized);
        Assert.Equal(
            expected.Fingerprint,
            PeerSupportCertificate.GetFingerprint(expected.Certificate));
        Assert.True(PeerSupportCertificate.MatchesFingerprint(
            expected.Certificate,
            separated));
        Assert.False(PeerSupportCertificate.MatchesFingerprint(
            different.Certificate,
            expected.Fingerprint));

        var changed = expected.Fingerprint[0] == '0'
            ? $"1{expected.Fingerprint[1..]}"
            : $"0{expected.Fingerprint[1..]}";
        Assert.False(PeerSupportCertificate.MatchesFingerprint(
            expected.Certificate,
            changed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")]
    public void Fingerprint_RejectsMalformedValues(string? value)
    {
        Assert.False(PeerSupportCertificate.TryNormalizeFingerprint(
            value,
            out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public async Task MutualTls_AcceptsExactPinsOnBothSides()
    {
        using var clientCertificate = PeerSupportCertificate.CreateEphemeral();
        using var serverCertificate = PeerSupportCertificate.CreateEphemeral();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await client.ConnectAsync(
                IPAddress.Loopback,
                endpoint.Port,
                timeout.Token);
            using var server = await acceptTask;

            var serverAuthentication = PeerSupportTls.AuthenticateAsServerAsync(
                server.GetStream(),
                serverCertificate,
                clientCertificate.Fingerprint,
                timeout.Token);
            var clientAuthentication = PeerSupportTls.AuthenticateAsClientAsync(
                client.GetStream(),
                clientCertificate,
                serverCertificate.Fingerprint,
                timeout.Token);
            var connections = await Task.WhenAll(
                serverAuthentication,
                clientAuthentication);
            await using var serverConnection = connections[0];
            await using var clientConnection = connections[1];

            Assert.Equal(
                clientCertificate.Fingerprint,
                serverConnection.RemoteCertificateFingerprint);
            Assert.Equal(
                serverCertificate.Fingerprint,
                clientConnection.RemoteCertificateFingerprint);

            var sent = "mutual TLS is active"u8.ToArray();
            await clientConnection.Stream.WriteAsync(sent, timeout.Token);
            var received = new byte[sent.Length];
            await serverConnection.Stream.ReadExactlyAsync(
                received,
                timeout.Token);
            Assert.Equal(sent, received);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task MutualTls_RejectsAValidButWrongServerPin()
    {
        using var clientCertificate = PeerSupportCertificate.CreateEphemeral();
        using var serverCertificate = PeerSupportCertificate.CreateEphemeral();
        using var unrelatedCertificate = PeerSupportCertificate.CreateEphemeral();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await client.ConnectAsync(
                IPAddress.Loopback,
                endpoint.Port,
                timeout.Token);
            using var server = await acceptTask;

            var serverAuthentication = PeerSupportTls.AuthenticateAsServerAsync(
                server.GetStream(),
                serverCertificate,
                clientCertificate.Fingerprint,
                timeout.Token);
            var clientAuthentication = PeerSupportTls.AuthenticateAsClientAsync(
                client.GetStream(),
                clientCertificate,
                unrelatedCertificate.Fingerprint,
                timeout.Token);

            var clientError = await Record.ExceptionAsync(async () =>
                await clientAuthentication.WaitAsync(timeout.Token));
            var serverError = await Record.ExceptionAsync(async () =>
                await serverAuthentication.WaitAsync(timeout.Token));

            Assert.True(
                clientError is AuthenticationException or IOException,
                $"Unexpected client TLS result: {clientError}");
            Assert.True(
                serverError is null or AuthenticationException or IOException,
                $"Unexpected server TLS result: {serverError}");
            if (serverAuthentication.IsCompletedSuccessfully)
            {
                var serverConnection = await serverAuthentication;
                await serverConnection.DisposeAsync();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task BinaryFrame_RoundTripsWithoutCompression()
    {
        var payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var original = new PeerSupportFrame(
            PeerSupportFrameType.Data,
            logicalStreamId: 17,
            sequence: 23,
            ack: 19,
            payload);
        await using var wire = new MemoryStream();

        await PeerSupportProtocol.WriteFrameAsync(
            wire,
            original,
            CancellationToken.None);

        Assert.Equal(0, wire.GetBuffer()[6] & 0x01);
        wire.Position = 0;
        var received = await PeerSupportProtocol.ReadFrameAsync(
            wire,
            CancellationToken.None);

        Assert.Equal(original.Type, received.Type);
        Assert.Equal(original.LogicalStreamId, received.LogicalStreamId);
        Assert.Equal(original.Sequence, received.Sequence);
        Assert.Equal(original.Ack, received.Ack);
        Assert.Equal(payload, received.Payload.ToArray());
        Assert.Equal(original.Sha256, received.Sha256);
        Assert.NotEqual(
            original.Sha256,
            new PeerSupportFrame(
                PeerSupportFrameType.Data,
                original.LogicalStreamId + 1,
                original.Sequence,
                original.Ack,
                payload).Sha256);
        Assert.NotEqual(
            original.Sha256,
            new PeerSupportFrame(
                original.Type,
                original.LogicalStreamId,
                original.Sequence + 1,
                original.Ack,
                payload).Sha256);
    }

    [Fact]
    public async Task BinaryFrame_RoundTripsBrotliCompressedPayload()
    {
        var payload = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat(
                "repeated diagnostics line with stable fields\n",
                4_096)));
        var original = new PeerSupportFrame(
            PeerSupportFrameType.Data,
            logicalStreamId: 3,
            sequence: 2,
            ack: 1,
            payload);
        await using var wire = new MemoryStream();

        await PeerSupportProtocol.WriteFrameAsync(
            wire,
            original,
            CancellationToken.None);

        Assert.Equal(0x01, wire.GetBuffer()[6] & 0x01);
        Assert.True(wire.Length < payload.Length);
        wire.Position = 0;
        var received = await PeerSupportProtocol.ReadFrameAsync(
            wire,
            CancellationToken.None);

        Assert.Equal(payload, received.Payload.ToArray());
        Assert.Equal(original.Sha256, received.Sha256);
    }

    [Fact]
    public async Task BinaryFrames_AcceleratedHourOfMetricsRoundTrips()
    {
        const int frameCount = 60 * 60 / 2;
        await using var wire = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        for (var index = 1; index <= frameCount; index++)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"sample\":{index},\"rttMs\":42," +
                "\"lossPercent\":0.25,\"jitterMs\":1.5}}\n");
            await PeerSupportProtocol.WriteFrameAsync(
                wire,
                new PeerSupportFrame(
                    PeerSupportFrameType.Data,
                    logicalStreamId: 3,
                    sequence: (ulong)index,
                    ack: 0,
                    payload),
                timeout.Token);
        }

        wire.Position = 0;
        for (var index = 1; index <= frameCount; index++)
        {
            var frame = await PeerSupportProtocol.ReadFrameAsync(
                wire,
                timeout.Token);
            Assert.Equal(PeerSupportFrameType.Data, frame.Type);
            Assert.Equal(3U, frame.LogicalStreamId);
            Assert.Equal((ulong)index, frame.Sequence);
            Assert.Contains(
                $"\"sample\":{index}",
                Encoding.UTF8.GetString(frame.Payload.Span),
                StringComparison.Ordinal);
        }

        Assert.Equal(wire.Length, wire.Position);
    }

    [Fact]
    public async Task BinaryFrame_RejectsInvalidTypeSpecificShape()
    {
        var invalidFrames = new[]
        {
            new PeerSupportFrame(
                PeerSupportFrameType.Data,
                logicalStreamId: 0,
                sequence: 1,
                ack: 0,
                "data"u8.ToArray()),
            new PeerSupportFrame(
                PeerSupportFrameType.Hello,
                logicalStreamId: 1,
                sequence: 1,
                ack: 0,
                "{}"u8.ToArray()),
            new PeerSupportFrame(
                PeerSupportFrameType.Ack,
                logicalStreamId: 0,
                sequence: 1,
                ack: 1,
                "not-empty"u8.ToArray()),
            new PeerSupportFrame(
                PeerSupportFrameType.Manifest,
                logicalStreamId: 0,
                sequence: 1,
                ack: 0,
                ReadOnlyMemory<byte>.Empty),
            new PeerSupportFrame(
                PeerSupportFrameType.CompleteStream,
                logicalStreamId: 1,
                sequence: 1,
                ack: 0,
                "not-empty"u8.ToArray()),
            new PeerSupportFrame(
                PeerSupportFrameType.Heartbeat,
                logicalStreamId: 0,
                sequence: 1,
                ack: 0,
                "not-empty"u8.ToArray()),
            new PeerSupportFrame(
                PeerSupportFrameType.CompleteSession,
                logicalStreamId: 0,
                sequence: 1,
                ack: 0,
                "not-empty"u8.ToArray())
        };

        foreach (var frame in invalidFrames)
        {
            await using var wire = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PeerSupportProtocol.WriteFrameAsync(
                    wire,
                    frame,
                    CancellationToken.None));
            Assert.Equal(0, wire.Length);
        }
    }

    [Fact]
    public async Task BinaryFrames_AcceptCancelReasonAndEmptyCompletion()
    {
        var frames = new[]
        {
            new PeerSupportFrame(
                PeerSupportFrameType.Cancel,
                logicalStreamId: 0,
                sequence: 1,
                ack: 0,
                PeerSupportProtocol.SerializeJson(new { reason = "target_changed" })),
            new PeerSupportFrame(
                PeerSupportFrameType.CompleteSession,
                logicalStreamId: 0,
                sequence: 2,
                ack: 1,
                ReadOnlyMemory<byte>.Empty)
        };
        await using var wire = new MemoryStream();

        foreach (var frame in frames)
        {
            await PeerSupportProtocol.WriteFrameAsync(
                wire,
                frame,
                CancellationToken.None);
        }

        wire.Position = 0;
        foreach (var expected in frames)
        {
            var received = await PeerSupportProtocol.ReadFrameAsync(
                wire,
                CancellationToken.None);
            Assert.Equal(expected.Type, received.Type);
            Assert.Equal(expected.LogicalStreamId, received.LogicalStreamId);
            Assert.Equal(expected.Payload.ToArray(), received.Payload.ToArray());
        }
    }

    [Fact]
    public void HelloValidation_RejectsMissingFieldsInvalidIdentityAndStaleTimestamp()
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var valid = new PeerSupportHello
        {
            SessionId = Guid.NewGuid(),
            SenderIdentityId = sender.ToString("D"),
            RecipientIdentityId = recipient.ToString("D"),
            StartedAtUtc = now,
            ResumeAfterSequence = 0
        };

        PeerSupportProtocol.ValidateHello(valid, now);

        var missingProtocol = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId = valid.SessionId,
            senderIdentityId = valid.SenderIdentityId,
            recipientIdentityId = valid.RecipientIdentityId,
            startedAtUtc = valid.StartedAtUtc
        });
        Assert.Throws<JsonException>(() =>
            PeerSupportProtocol.DeserializeJson<PeerSupportHello>(missingProtocol));

        Assert.Throws<InvalidDataException>(() =>
            PeerSupportProtocol.ValidateHello(
                valid with { SenderIdentityId = @"..\..\outside" },
                now));
        Assert.Throws<InvalidDataException>(() =>
            PeerSupportProtocol.ValidateHello(
                valid with { RecipientIdentityId = valid.SenderIdentityId },
                now));
        Assert.Throws<InvalidDataException>(() =>
            PeerSupportProtocol.ValidateHello(
                valid with { StartedAtUtc = now.AddDays(-8) },
                now));
        Assert.Throws<InvalidDataException>(() =>
            PeerSupportProtocol.ValidateHello(
                valid with { StartedAtUtc = now.AddDays(8) },
                now));
    }

    [Fact]
    public void ReceiverFileNames_CannotBeControlledByManifestDisplayName()
    {
        var manifest = new PeerSupportManifest
        {
            SessionId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SenderIdentityId = Guid.NewGuid().ToString("D"),
            Streams =
            [
                new PeerSupportManifestStream(
                    100,
                    PeerSupportLogKind.GameLog,
                    @"..\..\outside\stolen.txt",
                    null,
                    null)
            ]
        };

        PeerSupportProtocol.ValidateManifest(manifest);
        var receiverName = PeerSupportProtocol.GetReceiverFileName(
            manifest.Streams[0].Kind,
            manifest.Streams[0].LogicalStreamId);

        Assert.Equal("game-0000100.log", receiverName);
        Assert.Equal(receiverName, Path.GetFileName(receiverName));
        Assert.False(Path.IsPathRooted(receiverName));
        Assert.DoesNotContain("..", receiverName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", receiverName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", receiverName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryFrame_RejectsCorruptedPayloadHash()
    {
        var original = new PeerSupportFrame(
            PeerSupportFrameType.Data,
            logicalStreamId: 1,
            sequence: 1,
            ack: 0,
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        await using var wire = new MemoryStream();
        await PeerSupportProtocol.WriteFrameAsync(
            wire,
            original,
            CancellationToken.None);
        var bytes = wire.ToArray();
        bytes[^1] ^= 0x7f;
        await using var corrupted = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PeerSupportProtocol.ReadFrameAsync(
                corrupted,
                CancellationToken.None));
    }

    [Fact]
    public async Task BinaryFrame_RejectsOversizedWriteAndDeclaredRead()
    {
        var oversizedPayload =
            new byte[PeerSupportProtocol.MaxFramePayloadBytes + 1];
        var oversizedFrame = new PeerSupportFrame(
            PeerSupportFrameType.Data,
            logicalStreamId: 1,
            sequence: 1,
            ack: 0,
            oversizedPayload);
        await using var writeTarget = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PeerSupportProtocol.WriteFrameAsync(
                writeTarget,
                oversizedFrame,
                CancellationToken.None));

        var header = CreateHeader(
            flags: 0,
            encodedLength: PeerSupportProtocol.MaxFramePayloadBytes + 1,
            decodedLength: PeerSupportProtocol.MaxFramePayloadBytes + 1);
        await using var readSource = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PeerSupportProtocol.ReadFrameAsync(
                readSource,
                CancellationToken.None));
    }

    [Fact]
    public async Task BinaryFrame_RejectsCompressedExpansionBeyondDeclaredBound()
    {
        var expansion =
            new byte[PeerSupportProtocol.MaxFramePayloadBytes + 1];
        Array.Fill(expansion, (byte)'A');
        byte[] encoded;
        await using (var output = new MemoryStream())
        {
            await using (var brotli = new BrotliStream(
                             output,
                             CompressionLevel.SmallestSize,
                             leaveOpen: true))
            {
                await brotli.WriteAsync(expansion);
            }

            encoded = output.ToArray();
        }

        Assert.InRange(
            encoded.Length,
            1,
            PeerSupportProtocol.MaxFramePayloadBytes);
        var header = CreateHeader(
            flags: 0x01,
            encodedLength: encoded.Length,
            decodedLength: PeerSupportProtocol.MaxFramePayloadBytes);
        await using var wire = new MemoryStream();
        await wire.WriteAsync(header);
        await wire.WriteAsync(encoded);
        wire.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PeerSupportProtocol.ReadFrameAsync(
                wire,
                CancellationToken.None));
    }

    [Fact]
    public void ReplayGuard_RejectsZeroDuplicatesAndOlderSequences()
    {
        var guard = new PeerSupportReplayGuard(resumeAfterSequence: 41);

        Assert.False(guard.TryAccept(0));
        Assert.False(guard.TryAccept(40));
        Assert.False(guard.TryAccept(41));
        Assert.True(guard.TryAccept(42));
        Assert.False(guard.TryAccept(42));
        Assert.True(guard.TryAccept(44));
        Assert.False(guard.TryAccept(43));
        Assert.Equal(44UL, guard.HighestAcceptedSequence);
    }

    private static byte[] CreateHeader(
        byte flags,
        int encodedLength,
        int decodedLength)
    {
        var header = new byte[PeerSupportProtocol.HeaderSize];
        "MPDG"u8.CopyTo(header);
        header[4] = PeerSupportProtocol.ProtocolVersion;
        header[5] = (byte)PeerSupportFrameType.Data;
        header[6] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12, 8), 1);
        BinaryPrimitives.WriteInt32BigEndian(
            header.AsSpan(28, 4),
            encodedLength);
        BinaryPrimitives.WriteInt32BigEndian(
            header.AsSpan(32, 4),
            decodedLength);
        return header;
    }
}
