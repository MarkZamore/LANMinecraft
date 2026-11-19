using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The game is handed a URL, not a port, and the launcher has to be the one
/// answering at it.
/// </summary>
/// <remarks>
/// The launcher restarts itself after an update, seconds after the copy it
/// replaces let go of the socket, and Windows does not hand the port on that
/// quickly. It used to give up there and go on writing the port it had asked
/// for into the registry the game reads: the game asked, was refused, and
/// everybody wore the default skin until the next launch - with one line in the
/// launcher log to say so. That is what happened to both players on the night
/// of the 28th, right after the update they had just installed.
/// </remarks>
public sealed class SkinEndpointPortTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-skin-port-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task WhenTheUsualPortIsTaken_TheSkinIsStillServedAndTheRegistrySaysWhere()
    {
        var held = new TcpListener(IPAddress.Loopback, SkinService.PreferredHttpPort);
        var wasFree = TryHold(held);
        try
        {
            await using var service = CreateService(out var paths, out var uuid);
            await service.StartAsync();

            var url = ReadUrl(paths, uuid);
            var port = new Uri(url).Port;
            Assert.NotEqual(SkinService.PreferredHttpPort, port);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var served = await client.GetByteArrayAsync(url);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_root, "skin.png")), served);
        }
        finally
        {
            if (wasFree) held.Stop();
        }
    }

    /// <summary>And when it is free, it is the port the game is told about.</summary>
    [Fact]
    public async Task WhenTheUsualPortIsFree_ItIsTheOneUsed()
    {
        // Somebody else on this machine may hold it - the launcher itself, for
        // one - and then there is nothing to assert about the usual port.
        var probe = new TcpListener(IPAddress.Loopback, SkinService.PreferredHttpPort);
        if (!TryHold(probe)) return;
        probe.Stop();

        await using var service = CreateService(out var paths, out var uuid);
        await service.StartAsync();
        Assert.Equal(SkinService.PreferredHttpPort, new Uri(ReadUrl(paths, uuid)).Port);
    }

    private static bool TryHold(TcpListener listener)
    {
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private SkinService CreateService(out AppPaths paths, out string uuid)
    {
        Directory.CreateDirectory(_root);
        paths = new AppPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SkinRegistryFile)!);
        var service = new SkinService(
            paths,
            new Logger(paths.LogFile),
            new InMemoryPeerTransport(new InMemoryPeerNetwork(), Sender(), "MarkZamore"));
        uuid = "06c83c9e-980b-47d5-b7be-23d2bb649068";
        service.SelectLocalSkin(new AppSettings(), uuid, WriteSkin());
        return service;
    }

    private static SteamId64 Sender()
    {
        Assert.True(SteamId64.TryFrom(76561198256236531UL, out var id));
        return id;
    }

    /// <summary>The old layout, whose header is the whole of what is read.</summary>
    private string WriteSkin()
    {
        var path = Path.Combine(_root, "skin.png");
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        "IHDR"u8.ToArray().CopyTo(bytes, 12);
        BitConverter.GetBytes(
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(64)).CopyTo(bytes, 16);
        BitConverter.GetBytes(
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(32)).CopyTo(bytes, 20);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string ReadUrl(AppPaths paths, string uuid)
    {
        var line = Assert.Single(
            File.ReadAllLines(paths.SkinRegistryFile),
            entry => entry.StartsWith(uuid, StringComparison.OrdinalIgnoreCase));
        return line.Split('|')[3];
    }
}
