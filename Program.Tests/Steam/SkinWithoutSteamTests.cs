using System.Buffers.Binary;
using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Choosing a skin does not wait for Steam.
/// </summary>
/// <remarks>
/// A UUID is what a skin is filed under so other players can ask for it, and it
/// comes from Steam. It is not what makes a PNG a skin, and it is not needed to
/// remember which file was picked - so with Steam down the choice still
/// happens, a file that is not a skin is still refused on the spot, and only
/// the filing waits. The launch fills it in, and the launch needs Steam anyway,
/// so nobody ever sees the gap.
/// </remarks>
public sealed class SkinWithoutSteamTests : IDisposable
{
    private const string Uuid = "06c83c9e-980b-47d5-b7be-23d2bb649068";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"minecraft-skin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    [Fact]
    public void WithNoIdentityYet_TheChoiceIsTakenAndRemembered()
    {
        var (service, _) = CreateService();
        var settings = new AppSettings();

        var announcement = service.SelectLocalSkin(settings, "", WriteSkin());

        Assert.True(announcement.IsAvailable);
        Assert.Equal("classic", announcement.Model);
        Assert.False(string.IsNullOrWhiteSpace(settings.SkinPath));
    }

    /// <summary>
    /// And it reaches the registry the moment there is a name to file it under.
    /// This is the half that was missing: the file had been read, so the
    /// refresh took it for work already done and returned before filing it.
    /// </summary>
    [Fact]
    public void OnceTheIdentityArrives_TheSkinIsFiled()
    {
        var (service, paths) = CreateService();
        var settings = new AppSettings();
        service.SelectLocalSkin(settings, "", WriteSkin());

        service.RefreshLocalSkin(settings, Uuid);

        Assert.Contains(Uuid, File.ReadAllText(paths.SkinRegistryFile), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file that is not a skin is still refused while the player is looking
    /// at the dialog, not silently at the next launch.
    /// </summary>
    [Fact]
    public void WithNoIdentityYet_ANonSkinIsStillRefused()
    {
        var (service, _) = CreateService();
        var notASkin = Path.Combine(_root, "photo.png");
        File.WriteAllBytes(notASkin, [0x89, 0x50, 0x4E, 0x47]);

        Assert.Throws<InvalidDataException>(() => service.SelectLocalSkin(new AppSettings(), "", notASkin));
    }

    private (SkinService Service, AppPaths Paths) CreateService()
    {
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SkinRegistryFile)!);
        var service = new SkinService(
            paths,
            new Logger(paths.LogFile),
            new InMemoryPeerTransport(new InMemoryPeerNetwork(), Sender(), "MarkZamore"));
        return (service, paths);
    }

    private static SteamId64 Sender()
    {
        Assert.True(SteamId64.TryFrom(76561198256236531UL, out var id));
        return id;
    }

    /// <summary>The old 64x32 layout, whose header is the whole of what is read.</summary>
    private string WriteSkin()
    {
        var path = Path.Combine(_root, "skin.png");
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        "IHDR"u8.ToArray().CopyTo(bytes, 12);
        BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(64)).CopyTo(bytes, 16);
        BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(32)).CopyTo(bytes, 20);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
