namespace Minecraft;

/// <summary>One thing worth telling the player about somebody else.</summary>
public sealed record PeerNotice(string Title, string Body);

/// <summary>
/// Watches the list of friends for the two moments worth a notification: one of
/// them opening the launcher, and one of them starting a build.
/// </summary>
/// <remarks>
/// The list is not a stream of events. The window re-applies the whole of it
/// every two seconds whether anything moved or not, so the moments have to be
/// worked out here by comparing each pass with what the last one said. That is
/// also why this holds a memory of people who are no longer in the list: a peer
/// can drop out because Steam stopped serving their presence for a few minutes
/// and come straight back, and the player should not be told twice that the
/// same friend has arrived.
///
/// Nothing here touches Windows. What comes out is a list of notices, and
/// whoever asked decides what to do with them - which is what makes the rules
/// below testable without a screen.
/// </remarks>
public sealed class PeerArrivalNotifier(Func<DateTimeOffset>? clock = null)
{
    /// <summary>What is said when a friend opens the launcher.</summary>
    public const string EnteredLauncher = "Зашёл в LANMinecraft";

    /// <summary>What is said when a friend starts a build, before its name.</summary>
    public const string EnteredBuildPrefix = "Зашёл в ";

    // Long enough to swallow a friend who fell out of the list because their
    // presence went quiet and was read again a moment later; short enough that
    // somebody who really did close the launcher and open it again is
    // announced. The directory itself waits three minutes before giving up on
    // a silent peer, so anything faster than that was never a real departure.
    private static readonly TimeSpan ArrivalCooldown = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);
    private readonly Dictionary<ulong, Seen> _seen = [];

    // Whoever is already there when the launcher opens is not news, and the
    // first look is what says who that was. There is no waiting period: a
    // friend who arrives a second after the window is up is announced a second
    // after the window is up.
    private bool _lookedOnce;

    /// <summary>
    /// Reads the list as it stands and says what has happened since the last
    /// read.
    /// </summary>
    public IReadOnlyList<PeerNotice> Observe(IReadOnlyList<PeerViewModel> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var now = _now();
        var firstLook = !_lookedOnce;
        _lookedOnce = true;
        var notices = new List<PeerNotice>();

        foreach (var peer in peers)
        {
            var id = peer.SteamId.Value;
            // A friend who is in Minecraft without this launcher, and one whose
            // presence has not been read yet, are both rows that say nothing
            // about a launcher being open. The second becomes a real peer a few
            // seconds later, and that is the moment worth announcing.
            var inLauncher = !peer.IsOutsideLauncher && peer.IsPresenceKnown;
            var build = peer.IsMinecraftRunning ? (peer.PackName ?? "").Trim() : "";
            var known = _seen.TryGetValue(id, out var remembered);
            var seen = known ? remembered : Seen.Never;

            // Somebody already inside a build the first time we lay eyes on
            // them did not open their launcher this second: starting one takes
            // longer than the two seconds between looks, so we would have found
            // them idle or preparing first. This is what catches the friend
            // whose presence Steam served to us late - they were there all
            // along, and the list simply had not reached them yet.
            var alreadyPlaying = !known && build.Length > 0;
            var quiet = firstLook || alreadyPlaying;

            var arriving = !quiet && inLauncher && !seen.InLauncher &&
                           now - seen.Announced >= ArrivalCooldown;
            // The build a friend has merely chosen is published the whole time
            // they are idle, so what counts is the one they are actually in.
            var starting = !quiet && build.Length > 0 &&
                           !string.Equals(build, seen.Build, StringComparison.Ordinal);

            if (arriving) seen = seen with { Announced = now };
            // Somebody first seen already inside a build is one piece of news
            // and not two: naming the build says everything the bare arrival
            // would have said, and more.
            if (starting) notices.Add(new PeerNotice(peer.PeerName, EnteredBuildPrefix + build));
            else if (arriving) notices.Add(new PeerNotice(peer.PeerName, EnteredLauncher));

            _seen[id] = seen with { InLauncher = inLauncher, Build = build };
        }

        var present = peers.Select(peer => peer.SteamId.Value).ToHashSet();
        foreach (var id in _seen.Keys.ToList())
        {
            // Gone from the list. The build they were in is left as it was
            // rather than cleared: if this was a gap in their presence and not
            // a departure, they will come back still in it, and clearing it
            // here is what would announce it a second time.
            if (!present.Contains(id)) _seen[id] = _seen[id] with { InLauncher = false };
        }

        return notices;
    }

    private readonly record struct Seen(bool InLauncher, string Build, DateTimeOffset Announced)
    {
        public static Seen Never { get; } = new(false, "", DateTimeOffset.MinValue);
    }
}
