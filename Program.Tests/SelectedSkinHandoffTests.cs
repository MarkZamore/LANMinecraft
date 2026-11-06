using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Minecraft;

namespace Minecraft.Tests;

public sealed class SelectedSkinHandoffTests : IDisposable
{
    private const string Uuid = "06c83c9e-980b-47d5-b7be-23d2bb649068";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "selected-skin-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData("classic")]
    [InlineData("slim")]
    public async Task ASelectedSkin_KeepsItsBytesModelAndPlayerAcrossLauncherRestarts(string model)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var settingsService = new SettingsService(paths);
        var settings = settingsService.Load();
        var skinPath = WriteSkin(model);
        var bytes = File.ReadAllBytes(skinPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var identity = new LocalIdentityContext { MinecraftUuid = Uuid, PlayerUuid = Guid.Parse(Uuid) };

        await using (var selecting = CreateService(paths))
        {
            var selected = selecting.SelectLocalSkin(settings, Uuid, skinPath);
            Assert.Equal(new SkinAnnouncement(true, hash, model), selected);
            settingsService.Save(settings);
        }

        // A fresh service, as on the next ordinary Play, must reconstruct the
        // selected asset from settings rather than rely on the preview cache.
        await using var restarted = CreateService(paths);
        var reloaded = settingsService.Load();
        var registry = restarted.PrepareRegistry(reloaded, identity);
        await restarted.StartAsync();
        var fields = Assert.Single(File.ReadAllLines(registry)).Split('|');
        Assert.Equal(Uuid, fields[0]);
        Assert.Equal(hash, fields[1]);
        Assert.Equal(model, fields[2]);
        Assert.Equal(skinPath, reloaded.SkinPath);
        Assert.Equal(Uuid, identity.MinecraftUuid);
        Assert.Equal(Guid.Parse(Uuid), identity.PlayerUuid);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(bytes, await http.GetByteArrayAsync(fields[3]));

        // This is the existing peer protocol's payload, not evidence that a
        // second Minecraft renderer displayed it.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.SerializeToUtf8Bytes(new SkinRequest
        {
            PlayerUuid = Uuid,
            Sha256 = hash
        }, options);
        using var responseStream = new MemoryStream();
        await restarted.HandleIncomingAsync(responseStream, request, CancellationToken.None);
        responseStream.Position = 0;
        var response = PortableProtocol.Deserialize<SkinResponse>(
            await PortableProtocol.ReadFrameAsync(responseStream, CancellationToken.None), options);
        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal(Uuid, response.PlayerUuid);
        Assert.Equal(hash, response.Sha256);
        Assert.Equal(model, response.Model);
        using var payload = new MemoryStream();
        await responseStream.CopyToAsync(payload);
        Assert.Equal(bytes, payload.ToArray());
    }

    private static SkinService CreateService(AppPaths paths)
    {
        Assert.True(SteamId64.TryFrom(76561198256236531UL, out var peer));
        return new SkinService(paths, new Logger(paths.LogFile),
            new InMemoryPeerTransport(new InMemoryPeerNetwork(), peer, "Player"));
    }

    [Fact]
    public async Task PeerSkinIsAdvertisedOnlyWhenItsVerifiedBytesAreReady()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var bytes = File.ReadAllBytes(WriteSkin("slim"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var network = new InMemoryPeerNetwork();
        await using var sender = network.CreateTransport(76561198256236531UL);
        await using var receiver = network.CreateTransport(76561198256236532UL);
        var accepted = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.ConnectionAccepted += (_, connection) => accepted.TrySetResult(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sender.StartListeningAsync(timeout.Token);
        await using var service = new SkinService(paths, new Logger(paths.LogFile), receiver);
        await service.StartAsync();
        service.ObservePeer(new PeerViewModel
        {
            SteamId = sender.LocalPeer, MinecraftUuid = Uuid, IsSkinAvailable = true,
            SkinSha256 = hash, SkinModel = "slim"
        });
        await using var connection = await accepted.Task.WaitAsync(timeout.Token);
        var stream = connection.Stream;
        await PortableProtocol.ReadFrameAsync(stream, timeout.Token);
        var legacyRegistry = service.PrepareRegistry(new AppSettings(), new LocalIdentityContext(), waitForPeerBytes: true);
        Assert.Equal(paths.LegacySkinRegistryFile, legacyRegistry);
        Assert.False(File.Exists(legacyRegistry));
        Assert.Contains(hash, File.ReadAllText(paths.SkinRegistryFile), StringComparison.Ordinal);

        await PortableProtocol.WriteJsonAsync(stream, new SkinResponse
        {
            Ok = true, PlayerUuid = Uuid, Sha256 = hash, Model = "slim", SizeBytes = bytes.Length
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web), timeout.Token);
        await stream.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        while (!File.Exists(legacyRegistry)) await Task.Delay(20, timeout.Token);
        var fields = Assert.Single(File.ReadAllLines(legacyRegistry)).Split('|');
        Assert.Equal(new[] { Uuid, hash, "slim" }, fields[..3]);
        using var http = new HttpClient();
        Assert.Equal(bytes, await http.GetByteArrayAsync(fields[3], timeout.Token));
    }

    private string WriteSkin(string model)
    {
        const int width = 64;
        var pixels = Enumerable.Repeat((byte)255, width * width * 4).ToArray();
        if (model == "slim")
        {
            foreach (var (x, y) in new[] { (54, 20), (46, 52) })
            for (var row = y; row < y + 12; row++)
            for (var column = x; column < x + 2; column++)
                pixels[(row * width + column) * 4 + 3] = 0;
        }
        var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_root, model + ".png");
        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }
}
