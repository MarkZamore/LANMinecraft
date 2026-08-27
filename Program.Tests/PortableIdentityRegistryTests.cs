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
    private const ulong GuestSteamId = 76561198256236531;

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

    private static PeerViewModel Peer(string name, string uuid, ulong steamId64 = 76561198000000000UL) => new()
    {
        SteamId = SteamId64.TryFrom(steamId64, out var id) ? id : default,
        PlayerName = name,
        MinecraftUuid = uuid
    };

    /// <summary>
    /// The name and UUID a line names, without the tunnel UUID that may follow
    /// them: most of these tests are about which accounts get a line at all.
    /// </summary>
    private static string Account(string line) => string.Join('|', line.Split('|').Take(2));

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

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")))
            .Select(Account)
            .ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Contains("Oscar|11111111-2222-4333-8444-555555555555", lines);
        Assert.Contains("Friend|22222222-2222-4333-8444-555555555555", lines);
    }

    /// <summary>
    /// A guest's line also names the UUID a Steam tunnel would admit them
    /// under, because e4steam stamps that one over the portable UUID on its way
    /// into the world and the adapter has to know it to undo the swap.
    /// </summary>
    [Fact]
    public void AGuestsLine_AlsoNamesWhatTheSteamTunnelWouldCallThem()
    {
        var service = Create();
        service.ObservePeer(Peer("MarkZamore", "06c83c9e-980b-47d5-b7be-23d2bb649068", GuestSteamId));

        var lines = File.ReadAllLines(service.Prepare(Me("anuvenn", "f0f5ec1a-14f5-47b6-9e27-b860f62c14e5")));

        Assert.Contains(
            "MarkZamore|06c83c9e-980b-47d5-b7be-23d2bb649068|eedf749f-0e25-39a2-8a84-60146b6343a0",
            lines);
    }

    /// <summary>
    /// Steam has no answer for everyone. A line of two fields is what the
    /// adapter always read, so a player without an account still gets one.
    /// </summary>
    [Fact]
    public void APlayerWithNoSteamAccount_KeepsTheLineTheAdapterAlwaysRead()
    {
        var service = Create();
        service.ObservePeer(Peer("Nameless", "22222222-2222-4333-8444-555555555555", steamId64: 0));

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")));

        Assert.Contains("Nameless|22222222-2222-4333-8444-555555555555", lines);
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
        service.ObservePeer(Peer("Steve", "22222222-2222-4333-8444-555555555555", GuestSteamId));
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

        var lines = File.ReadAllLines(service.Prepare(Me("Oscar", "11111111-2222-4333-8444-555555555555")))
            .Select(Account)
            .ToArray();

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

    /// <summary>
    /// Both halves of the file agree on its shape. The launcher writes it and
    /// the adapter, which is Java and builds elsewhere, reads it; a third field
    /// written but split off at two would leave every guest as a stranger again
    /// with nothing on screen to say so.
    /// </summary>
    [Fact]
    public void TheAdapter_ReadsAllThreeFieldsTheLauncherWrites()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortableIdentityProfiles.java"));

        Assert.Contains("line.split(\"\\\\|\", 3)", source, StringComparison.Ordinal);
        Assert.Contains("byTunnelUuid.get(id)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
