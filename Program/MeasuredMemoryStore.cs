using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Keeps what the game was measured holding, between runs of the launcher.
///
/// It is a cache and behaves like one: an unreadable or missing file is the
/// estimate the sizing rules had before any of this existed, and a failed write
/// costs the next launch that same estimate and nothing else. A game is
/// starting or has just ended whenever this is touched, so nothing here throws.
///
/// Everything is kept per pair of pack and machine, because a footprint belongs
/// to both. The same pack on a card two sizes smaller holds gigabytes more in
/// system memory - the driver keeps there what will not fit in the card - and
/// the same pack on a machine with half the memory is a machine that pages
/// rather than one that holds less. So the card and the installed memory are in
/// the key, and a file carried to another computer, or a card swapped in, is
/// simply a pair nobody has measured yet.
/// </summary>
/// <remarks>
/// The pack's own weight is not in the key: mods are added to a pack all the
/// time, and a key that changed with them would throw away every measurement on
/// the day a pack updates - which is exactly the day the estimate is least
/// trustworthy. A pack that really has changed weight corrects itself instead,
/// as its newest sessions push the older ones out of the few that are kept.
/// </remarks>
public sealed class MeasuredMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// How many sessions of one pair are kept. Enough that one strange evening
    /// - a recording program running beside the game, a world being generated -
    /// does not become the pack's number for good, and few enough that a pack
    /// which has genuinely grown is described by its new self within a week of
    /// playing.
    /// </summary>
    internal const int SessionsKept = 5;

    /// <summary>
    /// How many pairs the file holds. A player has a handful of packs and one
    /// machine; the rest is packs they have stopped playing and a card they no
    /// longer own, and the oldest of those is dropped rather than kept forever.
    /// </summary>
    internal const int PairsKept = 12;

    private readonly string _file;

    public MeasuredMemoryStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = Path.Combine(paths.Personal, "memory-measurements.json");
    }

    /// <summary>What this pack has been measured holding on this machine.</summary>
    public MeasuredMemoryProfile Recall(string? packRelativePath, VideoMemoryProfile video, int installedGb)
    {
        var key = KeyFor(packRelativePath, video, installedGb);
        if (key.Length == 0) return MeasuredMemoryProfile.Unknown;

        return Load().TryGetValue(key, out var sessions)
            ? MeasuredMemoryProfile.From(sessions)
            : MeasuredMemoryProfile.Unknown;
    }

    /// <summary>
    /// Writes one finished session down, if it says anything. Returns what the
    /// pair now stands at, so the caller can say in the log what the next launch
    /// will use.
    /// </summary>
    public MeasuredMemoryProfile Remember(
        string? packRelativePath, VideoMemoryProfile video, int installedGb, MemorySession session)
    {
        var key = KeyFor(packRelativePath, video, installedGb);
        if (key.Length == 0 || !session.IsWorthKeeping) return MeasuredMemoryProfile.Unknown;

        var everything = Load();
        var sessions = everything.TryGetValue(key, out var kept) ? kept : [];
        // Newest last, and only the last few survive: a pack that grew is
        // described by the evenings since it grew, not by the ones before.
        sessions = [.. sessions.Where(older => older.IsWorthKeeping).Append(session).TakeLast(SessionsKept)];
        everything[key] = sessions;
        // Re-inserted at the end so the pair just played is the newest, and the
        // pairs that fall off the front are the ones nobody has played since.
        var trimmed = everything
            .Where(pair => pair.Key != key)
            .TakeLast(PairsKept - 1)
            .Append(new KeyValuePair<string, List<MemorySession>>(key, sessions))
            .ToDictionary(StringComparer.Ordinal);
        Save(trimmed);
        return MeasuredMemoryProfile.From(sessions);
    }

    /// <summary>
    /// The pair a measurement belongs to: this build, on this card, with this
    /// much memory installed. Lower-cased because these are folder names, and
    /// Windows does not think two spellings of one folder are two folders.
    /// </summary>
    internal static string KeyFor(string? packRelativePath, VideoMemoryProfile video, int installedGb)
    {
        var pack = packRelativePath?.Trim().ToLowerInvariant() ?? "";
        if (pack.Length == 0 || installedGb <= 0) return "";
        // A card that could not be read is its own machine rather than a
        // sixteen gigabyte one: the sizing charges it nothing, and a
        // measurement taken under that rule must not be handed to a machine
        // whose card answered.
        return $"{pack}|card={(video.IsKnown ? video.DedicatedGb : -1)}|ram={installedGb}";
    }

    private Dictionary<string, List<MemorySession>> Load()
    {
        try
        {
            if (!File.Exists(_file)) return Empty();
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<MemorySession>>>(
                File.ReadAllText(_file), JsonOptions);
            // An empty or nonsense file is no better than none.
            if (stored is null || stored.Count == 0) return Empty();
            return stored
                .Where(pair => pair.Key.Length > 0 && pair.Value is { Count: > 0 })
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Empty();
        }
    }

    private void Save(Dictionary<string, List<MemorySession>> everything)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(everything, JsonOptions));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static Dictionary<string, List<MemorySession>> Empty() => new(StringComparer.Ordinal);
}
