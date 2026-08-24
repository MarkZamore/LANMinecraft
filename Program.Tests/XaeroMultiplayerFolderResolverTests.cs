namespace Minecraft.Tests;

/// <summary>
/// e4steam gives a host a new address every time they open their world, and
/// Xaero's minimap files waypoints under a folder named after that address. So
/// a guest ends up with one folder per session and the launcher has to find
/// them all - otherwise every session starts with an empty map, which is the
/// whole reason waypoint sync exists.
/// </summary>
public sealed class XaeroMultiplayerFolderResolverTests : IDisposable
{
    private const ulong Host = 76561198256236531;
    private const ulong OtherHost = 76561198050776152;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-xaero-{Guid.NewGuid():N}");

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

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(35UL, "z")]
    [InlineData(36UL, "10")]
    [InlineData(76561198256236531UL, "kxuogxe7bhv")]
    public void Base36_MatchesWhatTheAddressCarries(ulong value, string expected) =>
        Assert.Equal(expected, XaeroMultiplayerFolderResolver.ToBase36(value));

    [Fact]
    public void EverySessionWithAHost_IsFound_NewestFirst()
    {
        var game = CreateGameDirectory();
        var older = CreateSessionFolder(game, Host, "aaa", DateTime.UtcNow.AddHours(-2));
        var newer = CreateSessionFolder(game, Host, "bbb", DateTime.UtcNow);
        CreateSessionFolder(game, OtherHost, "ccc", DateTime.UtcNow.AddMinutes(-1));
        Directory.CreateDirectory(Path.Combine(
            XaeroMultiplayerFolderResolver.GetMinimapRoot(game), "Multiplayer_127.0.0.1"));

        Assert.True(SteamId64.TryFrom(Host, out var host));
        var folders = XaeroMultiplayerFolderResolver.FindHostFolders(game, host);

        Assert.Equal([newer, older], folders);
        Assert.Equal(newer, XaeroMultiplayerFolderResolver.FindCurrentHostFolder(game, host));
    }

    /// <summary>
    /// The address is what the provider escapes back into a folder name, so a
    /// round trip has to land on the very same folder.
    /// </summary>
    [Fact]
    public void TheResolvedAddress_RoundTripsToTheSameFolder()
    {
        var game = CreateGameDirectory();
        var folder = CreateSessionFolder(game, Host, "tok3n", DateTime.UtcNow);
        Assert.True(SteamId64.TryFrom(Host, out var host));

        var address = XaeroMultiplayerFolderResolver.FindCurrentHostAddress(game, host);

        Assert.NotNull(address);
        Assert.Equal($"s-{XaeroMultiplayerFolderResolver.ToBase36(Host)}-tok3n.steam", address);
        Assert.Equal(folder, Path.Combine(
            XaeroMultiplayerFolderResolver.GetMinimapRoot(game), $"Multiplayer_{address}"));
    }

    [Fact]
    public void AHostNeverJoined_HasNoFoldersAndAStablePlaceholder()
    {
        var game = CreateGameDirectory();
        Assert.True(SteamId64.TryFrom(Host, out var host));

        Assert.Empty(XaeroMultiplayerFolderResolver.FindHostFolders(game, host));
        Assert.Null(XaeroMultiplayerFolderResolver.FindCurrentHostAddress(game, host));
        Assert.Equal($"lanmc-{Host}", XaeroMultiplayerFolderResolver.PendingAddress(host));
        Assert.Empty(XaeroMultiplayerFolderResolver.PendingAddress(SteamId64.None));
    }

    /// <summary>
    /// A token can contain anything base36 produces, including the digits of
    /// another host, so the host segment is matched as a segment.
    /// </summary>
    [Fact]
    public void AnotherHostsSessionIsNotMistakenForThisOne()
    {
        var game = CreateGameDirectory();
        var marker = XaeroMultiplayerFolderResolver.ToBase36(Host);
        Directory.CreateDirectory(Path.Combine(
            XaeroMultiplayerFolderResolver.GetMinimapRoot(game),
            $"Multiplayer_s-{XaeroMultiplayerFolderResolver.ToBase36(OtherHost)}-{marker}.steam"));
        Assert.True(SteamId64.TryFrom(Host, out var host));

        Assert.Empty(XaeroMultiplayerFolderResolver.FindHostFolders(game, host));
    }

    private string CreateGameDirectory()
    {
        var game = Path.Combine(_root, "instance");
        Directory.CreateDirectory(XaeroMultiplayerFolderResolver.GetMinimapRoot(game));
        return game;
    }

    private static string CreateSessionFolder(string game, ulong host, string token, DateTime writtenAtUtc)
    {
        var folder = Path.Combine(
            XaeroMultiplayerFolderResolver.GetMinimapRoot(game),
            $"Multiplayer_s-{XaeroMultiplayerFolderResolver.ToBase36(host)}-{token}.steam");
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, writtenAtUtc);
        return folder;
    }
}
