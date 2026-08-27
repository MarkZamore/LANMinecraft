using System.Text.Json.Serialization;

namespace Minecraft;

/// <summary>
/// What one finished session actually held, as the launcher watched it.
///
/// The numbers come from <see cref="MinecraftProcessService"/>, which samples
/// the running game every thirty seconds and keeps the largest it saw: what the
/// process had asked the system to commit, and how much of that was resident.
/// The heap it was started with is written down beside them, because the whole
/// point of the record is the subtraction - a heap of 19456 MB inside a commit
/// of 26989 MB is 7533 MB of room beside it, and that room is the one number
/// the sizing rules have never been able to do better than estimate.
/// </summary>
/// <param name="CommittedMb">The largest commit the process was seen at.</param>
/// <param name="ResidentMb">How much of it was in memory at that moment.</param>
/// <param name="HeapMb">What <c>-Xmx</c> was set to for the session.</param>
/// <param name="Minutes">How long the game ran.</param>
/// <param name="When">When it ended, so a file read by hand makes sense.</param>
public readonly record struct MemorySession(
    int CommittedMb,
    int ResidentMb,
    int HeapMb,
    int Minutes,
    DateTimeOffset When)
{
    /// <summary>
    /// The shortest session worth believing. Limitless 8 takes about a minute
    /// to reach the menu with its 882 jars, and a client that has not opened a
    /// world yet has not built a single chunk buffer - which is most of what
    /// lives beside the heap. Ten minutes is twenty of the thirty-second
    /// samples, and a session that ends sooner than that was a launcher test,
    /// not an evening.
    /// </summary>
    public const int ShortestSessionMinutes = 10;

    /// <summary>
    /// What the game held outside its heap: class data, compiled code, thread
    /// stacks, and the buffers the graphics driver keeps in system memory.
    /// </summary>
    [JsonIgnore]
    public int BesideHeapMb => Math.Max(0, CommittedMb - HeapMb);

    /// <summary>
    /// Whether this session says anything about the pack rather than about the
    /// evening it happened on.
    /// </summary>
    /// <remarks>
    /// Three ways a session lies about the pack, and all three are thrown away.
    ///
    /// It can be too short to have reached the footprint at all - see above.
    ///
    /// It can have run on a machine that could not hold what the game asked
    /// for, and then the footprint is a negotiation with the page file rather
    /// than a measurement: the driver trims, the collector runs full
    /// collections of two seconds, and the pack never grows into what it would
    /// have taken. The test is the resident share, and it is drawn at a half
    /// rather than higher on purpose. A large heap is committed the instant the
    /// game starts and only becomes resident as it is filled, so a healthy
    /// session sits well under its own commit: the 26989 MB session this record
    /// was built for was 14687 MB resident - 54% - with most of the gap being a
    /// 19 GB heap it had not finished filling.
    ///
    /// And it can have run with a heap that was still growing. A heap above
    /// <see cref="MinecraftProcessService.SmallHeapCeilingMb"/> is started at
    /// its maximum, so every byte of it is inside the commit and the
    /// subtraction means what it says; a small one starts at a gigabyte on
    /// purpose, and subtracting its maximum would report a pack holding
    /// nothing beside its heap and hand a small machine a heap it cannot keep.
    /// </remarks>
    [JsonIgnore]
    public bool IsWorthKeeping =>
        Minutes >= ShortestSessionMinutes &&
        CommittedMb > 0 && ResidentMb > 0 && HeapMb > 0 &&
        BesideHeapMb > 0 &&
        ResidentMb * 2 >= CommittedMb &&
        MinecraftProcessService.InitialHeapMbFor(HeapMb) == HeapMb;

    /// <summary>Why this session is not being kept, for the log, or "".</summary>
    [JsonIgnore]
    public string WhyNotKept =>
        IsWorthKeeping ? ""
        : Minutes < ShortestSessionMinutes
            ? $"it ran {Minutes} min, under the {ShortestSessionMinutes} a footprint needs"
        : CommittedMb <= 0 || ResidentMb <= 0 || HeapMb <= 0 || BesideHeapMb <= 0
            ? "the process could not be measured"
        : ResidentMb * 2 < CommittedMb
            ? $"only {ResidentMb} MB of {CommittedMb} MB stayed in memory, so the machine was paging"
            : $"a heap of {HeapMb} MB starts small and grows, so what is beside it cannot be read off the commit";
}

/// <summary>
/// The room beside the heap as it has actually been measured on this machine,
/// for this pack: the answer the sizing rules use instead of their estimate
/// whenever there is one.
///
/// The estimate is a model of a pack - a base, a per-mod cost, a share of the
/// jar bytes - fitted to one measurement of one pack, and it charges the card's
/// shortfall on top of that. This does not model anything. It is what the game
/// on this machine was seen holding, which already contains whatever the driver
/// keeps in system memory, whatever the mods allocate off-heap and whatever
/// this particular Windows adds - so nothing is added to it for the card.
/// </summary>
/// <param name="BesideHeapMb">
/// The largest room beside the heap of the sessions being remembered. Largest
/// rather than typical: a reserve set too low is spent by the game anyway, over
/// the budget and into the memory the machine kept for itself, while one set a
/// gigabyte too high costs a gigabyte of heap out of a budget that has more.
/// </param>
/// <param name="Sessions">How many sessions that number stands on.</param>
public readonly record struct MeasuredMemoryProfile(int BesideHeapMb, int Sessions)
{
    /// <summary>
    /// A pack on a machine that has not been watched yet - the state every pair
    /// starts in, and the one the estimate exists for.
    /// </summary>
    public static MeasuredMemoryProfile Unknown { get; } = new(0, 0);

    /// <summary>False for <see cref="Unknown"/> alone.</summary>
    public bool IsKnown => BesideHeapMb > 0 && Sessions > 0;

    /// <summary>What a handful of kept sessions come to.</summary>
    public static MeasuredMemoryProfile From(IEnumerable<MemorySession> sessions)
    {
        var kept = sessions.Where(session => session.IsWorthKeeping).ToList();
        return kept.Count == 0
            ? Unknown
            : new MeasuredMemoryProfile(kept.Max(session => session.BesideHeapMb), kept.Count);
    }
}
