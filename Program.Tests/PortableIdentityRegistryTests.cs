using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Which player each in-game name belongs to.
/// </summary>
/// <remarks>
/// Offline Minecraft derives a player's UUID from their name alone, and the
/// world keeps their inventory in a file named after that UUID. So a player who
/// changed machines kept their things only for as long as they kept their name.
/// The launcher knows whose account each name is, because every peer announces
/// it; this is that knowledge, written where the running game can read it.
/// </remarks>
public sealed class PortableIdentityRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-identity-registry-{Guid.NewGuid():N}");

    private AppPaths Paths => new(_root);
    private PortableIdentityRegistryService Create() =>
        new(Paths, new Logger(Path.Combine(_root, "log.txt")));

    public void Dispose() => TempTree.Delete(_root);

    private static LocalIdentityContext Me(string name, string uuid) => new()
    {
        IdentityName = name,
        MinecraftUuid = uuid
    };

    private static PeerViewModel Peer(string name, string uuid) => new()
    {
        SteamId = SteamId64.TryFrom(76561198000000000UL, out var id) ? id : default,
        PlayerName = name,
        MinecraftUuid = uuid
    };

    [Fact]
    public void ThePlayerAtThisMachine_IsWrittenDown()
    {
        var service = Create();

        var path = service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555"));

        Assert.Equal(
            "Oscar|11111111-2222-4333-8444-555555555555",
            File.ReadAllText(path).Trim());
    }

    /// <summary>
    /// The host needs every guest's account, not just its own: the server it
    /// runs is what decides the UUID a joining player is admitted under.
    /// </summary>
    [Fact]
    public void EveryPeerSeen_IsWrittenDownToo()
    {
        var service = Create();
        service.ObservePeer(Peer("Friend", "22222222-2222-4333-8444-555555555555"));

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")));

        Assert.Equal(2, lines.Length);
        Assert.Contains("Oscar|11111111-2222-4333-8444-555555555555", lines);
        Assert.Contains("Friend|22222222-2222-4333-8444-555555555555", lines);
    }

    /// <summary>
    /// Two accounts under one name are left out of it. Choosing between them
    /// would hand one player the other's inventory, and the name's own UUID -
    /// what they both had before any of this - is the safe answer.
    /// </summary>
    [Fact]
    public void AName_TwoAccountsBothAnswerTo_IsNotClaimedByEither()
    {
        var service = Create();
        service.ObservePeer(Peer("Steve", "22222222-2222-4333-8444-555555555555"));
        service.ObservePeer(Peer("Steve", "33333333-2222-4333-8444-555555555555"));

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")));

        Assert.DoesNotContain(lines, line => line.StartsWith("Steve|", StringComparison.Ordinal));
        Assert.Contains("Oscar|11111111-2222-4333-8444-555555555555", lines);
    }

    /// <summary>
    /// A peer who renames keeps one line, not two: the account is what is being
    /// recorded, and it has one name at a time.
    /// </summary>
    [Fact]
    public void ARenamedPeer_ReplacesItsOwnLine()
    {
        var service = Create();
        service.ObservePeer(Peer("Before", "22222222-2222-4333-8444-555555555555"));
        service.ObservePeer(Peer("After", "22222222-2222-4333-8444-555555555555"));

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")));

        Assert.Equal(2, lines.Length);
        Assert.Contains("After|22222222-2222-4333-8444-555555555555", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Before|", StringComparison.Ordinal));
    }

    [Fact]
    public void APeerWithNothingToSay_IsIgnored()
    {
        var service = Create();
        service.ObservePeer(Peer("", "22222222-2222-4333-8444-555555555555"));
        service.ObservePeer(Peer("Nameless", ""));
        service.ObservePeer(Peer("Broken", "not-a-uuid"));

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")));

        Assert.Single(lines);
    }
}
