using System.IO;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class PackTransferTests
{
    [Fact]
    public async Task ConfirmedSteamTransferInstallsBeforeStoredAndRejectsDeclinedAndCorruptPayloads()
    {
        const ulong first = 76561198000000001;
        const ulong second = 76561198000000002;
        const string packName = "Steam Pack";
        var network = new InMemoryPeerNetwork();
        await using var senderTransport = network.CreateTransport(first, "Sender");
        await using var receiverTransport = network.CreateTransport(second, "Receiver");
        network.MakeFriends(first, second);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = testDeadline.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "pack-transfer-fixture-" + Guid.NewGuid().ToString("N"));
        var senderPaths = new AppPaths(Path.Combine(root, "sender"));
        var receiverPaths = new AppPaths(Path.Combine(root, "receiver"));
        senderPaths.Ensure();
        receiverPaths.Ensure();
        try
        {
            var source = senderPaths.CombineUnderPacks(packName);
            Directory.CreateDirectory(Path.Combine(source, "mods"));
            File.WriteAllText(Path.Combine(source, PackManifestService.ManifestFileName),
                """{"schemaVersion":1,"minecraftVersion":"1.21.1","loader":{"type":"fabric","version":"0.16.14"}}""");
            File.WriteAllText(Path.Combine(source, "mods", "sample.jar"), "fixture mod");
            var sourceWorld = Path.Combine(senderPaths.Worlds, "Private World");
            Directory.CreateDirectory(sourceWorld);
            File.WriteAllText(Path.Combine(sourceWorld, "level.dat"), "my progress");

            var senderArchive = CreateArchiveService(senderPaths);
            var receiverArchive = CreateArchiveService(receiverPaths);
            var offers = new List<PackTransferOffer>();
            var decisions = new Queue<bool>([false, true, true]);
            var activeDuringConfirmation = false;
            PackTransferService? receivingService = null;
            await using var receiver = new PackTransferService(receiverPaths, receiverTransport, receiverArchive,
                (offer, _) =>
                {
                    offers.Add(offer);
                    activeDuringConfirmation |= receivingService?.IsActive == true;
                    return Task.FromResult(decisions.Dequeue());
                });
            receivingService = receiver;
            var received = new List<string>();
            receiver.PackReceived += received.Add;
            await using var router = new PeerConnectionRouter(receiverTransport);
            router.Register(receiver);
            await router.StartAsync(token);
            await using var sender = new PackTransferService(senderPaths, senderTransport, senderArchive,
                (_, _) => Task.FromResult(false));
            var recipient = new PeerViewModel { SteamId = receiverTransport.LocalPeer };

            var decline = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sender.SendAsync(recipient, packName, token));
            Assert.Contains("отклонил", decline.Message);
            Assert.False(Directory.Exists(receiverPaths.CombineUnderPacks(packName)));
            await WaitForIdleAsync(receiver, token);

            await sender.SendAsync(recipient, packName, token);
            Assert.True(activeDuringConfirmation);
            var validOffer = offers[1];
            Assert.Equal(senderTransport.LocalPeer, validOffer.Sender);
            Assert.Equal("Sender", validOffer.SenderName);
            Assert.Equal(packName, validOffer.PackName);
            Assert.True(validOffer.ArchiveBytes > 0);
            Assert.Equal(packName, Assert.Single(received));
            var result = receiverPaths.CombineUnderPacks(packName);
            Assert.Equal("fixture mod", File.ReadAllText(Path.Combine(result, "mods", "sample.jar")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(receiverPaths.Worlds));
            Assert.Equal("my progress", File.ReadAllText(Path.Combine(sourceWorld, "level.dat")));
            using var sourceHash = new PackHashService(senderPaths);
            using var installedHash = new PackHashService(receiverPaths);
            Assert.Equal(await sourceHash.CalculateAsync(source, token), await installedHash.CalculateAsync(result, token));
            await WaitForIdleAsync(receiver, token);

            // A valid friend and an accepted offer still have to deliver the exact archive advertised.
            const string corruptName = "Corrupt Pack";
            var transferId = Guid.NewGuid();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await using (var connection = await senderTransport.ConnectAsync(receiverTransport.LocalPeer, PackTransferService.Protocol, token))
            {
                var stream = connection.Stream;
                await PortableProtocol.WriteJsonAsync(stream, new
                {
                    protocol = PackTransferService.Protocol,
                    protocolVersion = PortableFormat.ProtocolVersion,
                    transferId,
                    packName = corruptName,
                    bytes = 3,
                    sha256 = new string('0', 64)
                }, json, token);
                Assert.Equal("Accepted", await ReadStatusAsync(stream, transferId, token));
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
                await PortableProtocol.WriteJsonAsync(stream, new
                {
                    protocol = PackTransferService.Protocol,
                    protocolVersion = PortableFormat.ProtocolVersion,
                    transferId,
                    status = "Sent"
                }, json, token);
                Assert.Equal("Failed", await ReadStatusAsync(stream, transferId, token));
            }
            await WaitForIdleAsync(receiver, token);
            Assert.Equal(3, offers.Count);
            Assert.Single(received);
            Assert.False(Directory.Exists(receiverPaths.CombineUnderPacks(corruptName)));
            Assert.Empty(Directory.EnumerateDirectories(receiverPaths.Launcher, ".pack-sync-stage-plan-*"));
        }
        finally { TempTree.Delete(root); }
    }

    private static PackArchiveService CreateArchiveService(AppPaths paths) => new(paths, _ => true, _ => false);

    private static async Task<string?> ReadStatusAsync(Stream stream, Guid transferId, CancellationToken token)
    {
        using var frame = JsonDocument.Parse(await PortableProtocol.ReadFrameAsync(stream, token));
        Assert.Equal(transferId, frame.RootElement.GetProperty("transferId").GetGuid());
        return frame.RootElement.GetProperty("status").GetString();
    }

    private static async Task WaitForIdleAsync(PackTransferService service, CancellationToken token)
    {
        while (service.IsActive) await Task.Delay(10, token);
    }
}
