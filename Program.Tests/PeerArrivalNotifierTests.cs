using Xunit;

namespace Minecraft.Tests;

/// <summary>
/// The two moments worth telling a player about somebody else: a friend opening
/// the launcher, and a friend starting a build.
///
/// Neither arrives as an event. The window re-applies the whole list of friends
/// every two seconds whether anything moved or not, so both moments are read
/// out of the difference between one pass and the next - which is what makes
/// them easy to get wrong in the two directions that matter. Saying nothing
/// when a friend really has arrived is a feature that does not work; saying it
/// twice, or saying it about everybody who was already there when the launcher
/// opened, is worse than saying nothing at all.
/// </summary>
public sealed class PeerArrivalNotifierTests
{
    private const ulong FriendId = 76561198986239755;
    private const ulong OtherId = 76561198256236531;

    private DateTimeOffset _now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The first look has been taken, so anything that turns up now is news.
    /// No time passes: there is no waiting period to wait out.
    /// </summary>
    private PeerArrivalNotifier Settled()
    {
        var notifier = new PeerArrivalNotifier(() => _now);
        notifier.Observe([]);
        return notifier;
    }

    [Fact]
    public void TheFriendsWhoWereAlreadyThere_AreNotAnnounced()
    {
        var notifier = new PeerArrivalNotifier(() => _now);

        // The first look says who was already here. Nobody in it is news.
        Assert.Empty(notifier.Observe([InLauncher(FriendId, "Kazak"), InBuild(OtherId, "anuvenn", "LL8 Extended")]));
        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([InLauncher(FriendId, "Kazak"), InBuild(OtherId, "anuvenn", "LL8 Extended")]));
    }

    /// <summary>
    /// Steam serves one friend's presence a moment after the rest, and that is
    /// not them arriving. Somebody found already inside a build cannot have
    /// opened their launcher in the two seconds since the last look.
    /// </summary>
    [Fact]
    public void AFriendFoundAlreadyInABuild_IsNotAnnounced()
    {
        var notifier = Settled();

        Assert.Empty(notifier.Observe([InBuild(OtherId, "anuvenn", "LL8 Extended")]));

        // And having been recorded, a build they move to afterwards is news.
        _now = _now.AddSeconds(2);
        Assert.Equal(
            "Зашёл в TerraFirma Rebirth",
            Assert.Single(notifier.Observe([InBuild(OtherId, "anuvenn", "TerraFirma Rebirth")])).Body);
    }

    /// <summary>
    /// The whole point of dropping the waiting period: a friend who opens the
    /// launcher a moment after we open ours is announced then and there.
    /// </summary>
    [Fact]
    public void AFriendWhoArrivesSecondsAfterWeOpen_IsAnnouncedAtOnce()
    {
        var notifier = new PeerArrivalNotifier(() => _now);
        notifier.Observe([]);

        _now = _now.AddSeconds(2);

        Assert.Equal(
            new PeerNotice("Kazak", "Зашёл в LANMinecraft"),
            Assert.Single(notifier.Observe([InLauncher(FriendId, "Kazak")])));
    }

    [Fact]
    public void AFriendWhoOpensTheLauncher_IsAnnouncedOnce()
    {
        var notifier = Settled();

        var first = notifier.Observe([InLauncher(FriendId, "Kazak")]);

        Assert.Equal(new PeerNotice("Kazak", "Зашёл в LANMinecraft"), Assert.Single(first));
        // Every two seconds thereafter, and none of them is a second arrival.
        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([InLauncher(FriendId, "Kazak")]));
        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([InLauncher(FriendId, "Kazak")]));
    }

    [Fact]
    public void AFriendPlayingWithoutTheLauncher_IsNotAnnounced()
    {
        var notifier = Settled();
        var peer = InLauncher(FriendId, "Kazak");
        peer.IsOutsideLauncher = true;

        Assert.Empty(notifier.Observe([peer]));
    }

    [Fact]
    public void ARowWhosePresenceIsNotReadYet_WaitsUntilItIs()
    {
        var notifier = Settled();
        var unknown = InLauncher(FriendId, "Kazak");
        // Listed because Steam says they are in the shared app, but none of our
        // keys have arrived - so nothing yet says a launcher is open.
        unknown.ProtocolVersion = 0;

        Assert.Empty(notifier.Observe([unknown]));

        _now = _now.AddSeconds(10);
        Assert.Equal(
            new PeerNotice("Kazak", "Зашёл в LANMinecraft"),
            Assert.Single(notifier.Observe([InLauncher(FriendId, "Kazak")])));
    }

    [Fact]
    public void AFriendStartingABuild_IsAnnouncedWithItsName()
    {
        var notifier = Settled();
        notifier.Observe([InLauncher(FriendId, "Kazak")]);
        _now = _now.AddSeconds(2);

        var notices = notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]);

        Assert.Equal(new PeerNotice("Kazak", "Зашёл в LL8 Extended"), Assert.Single(notices));
        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]));
    }

    /// <summary>
    /// The build a friend has merely selected is published the whole time they
    /// are idle. Only the one they are actually in is worth announcing.
    /// </summary>
    [Fact]
    public void ABuildOnlyChosen_SaysNothing()
    {
        var notifier = Settled();
        var idle = InLauncher(FriendId, "Kazak");
        idle.PackName = "TerraFirma Rebirth";

        Assert.Equal("Зашёл в LANMinecraft", Assert.Single(notifier.Observe([idle])).Body);
        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([idle]));
    }

    [Fact]
    public void ChangingBuild_IsAnnouncedAgain()
    {
        var notifier = Settled();
        notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]);
        _now = _now.AddSeconds(2);

        var notices = notifier.Observe([InBuild(FriendId, "Kazak", "TerraFirma Rebirth")]);

        Assert.Equal("Зашёл в TerraFirma Rebirth", Assert.Single(notices).Body);
    }

    [Fact]
    public void LeavingAndStartingTheSameBuildAgain_IsAnnouncedAgain()
    {
        var notifier = Settled();
        notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]);
        _now = _now.AddSeconds(2);
        notifier.Observe([InLauncher(FriendId, "Kazak")]);
        _now = _now.AddSeconds(2);

        var notices = notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]);

        Assert.Equal("Зашёл в LL8 Extended", Assert.Single(notices).Body);
    }

    /// <summary>
    /// Steam stops serving a friend's presence now and then and the directory
    /// eventually drops them; the reading that follows is not an arrival, and a
    /// player watching the corner of their screen must not be told it is.
    /// </summary>
    [Fact]
    public void AFriendWhoFlickersOutAndBack_IsNotAnnouncedTwice()
    {
        var notifier = Settled();
        // Found idle, then in a build: two events, both news.
        Assert.Single(notifier.Observe([InLauncher(FriendId, "Kazak")]));
        _now = _now.AddSeconds(2);
        Assert.Equal(
            "Зашёл в LL8 Extended",
            Assert.Single(notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")])).Body);

        _now = _now.AddMinutes(3);
        Assert.Empty(notifier.Observe([]));

        _now = _now.AddSeconds(2);
        Assert.Empty(notifier.Observe([InBuild(FriendId, "Kazak", "LL8 Extended")]));
    }

    [Fact]
    public void AFriendWhoComesBackMuchLater_IsAnnouncedAgain()
    {
        var notifier = Settled();
        Assert.Single(notifier.Observe([InLauncher(FriendId, "Kazak")]));

        _now = _now.AddMinutes(30);
        Assert.Empty(notifier.Observe([]));

        _now = _now.AddSeconds(2);
        Assert.Equal(
            "Зашёл в LANMinecraft",
            Assert.Single(notifier.Observe([InLauncher(FriendId, "Kazak")])).Body);
    }

    private static PeerViewModel InLauncher(ulong steamId, string name)
    {
        Assert.True(SteamId64.TryFrom(steamId, out var id));
        return new PeerViewModel
        {
            SteamId = id,
            PlayerName = name,
            PersonaName = name,
            ProtocolVersion = PortableFormat.ProtocolVersion
        };
    }

    private static PeerViewModel InBuild(ulong steamId, string name, string build)
    {
        var peer = InLauncher(steamId, name);
        peer.PackName = build;
        peer.IsMinecraftRunning = true;
        return peer;
    }
}
