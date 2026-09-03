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

    // The friends who were already playing when the launcher opened are not
    // news. They arrive over the first seconds - Steam is read every five - and
    // announcing them would greet the player with a stack of notifications for
    // people who had been there all along.
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);
    private readonly Dictionary<ulong, Seen> _seen = [];
    private DateTimeOffset? _startedAt;

    /// <summary>
    /// Reads the list as it stands and says what has happened since the last
    /// read.
    /// </summary>
    public IReadOnlyList<PeerNotice> Observe(IReadOnlyList<PeerViewModel> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var now = _now();
        _startedAt ??= now;
        var quiet = now - _startedAt.Value < StartupQuiet;
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
            var seen = _seen.TryGetValue(id, out var known) ? known : Seen.Never;

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
