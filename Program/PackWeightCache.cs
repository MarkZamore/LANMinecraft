using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Remembers how many mods a pack carries inside its own jars, so the answer is
/// worked out once instead of before every window.
///
/// Counting them means opening every jar in the pack and reading its index -
/// nearly a thousand archives for this build, which is most of a second on a
/// machine whose antivirus reads each one over the launcher's shoulder. That
/// second used to be spent with nothing on screen, and it grew every time the
/// pack gained mods.
///
/// It is a cache and behaves like one: a missing or unreadable file costs one
/// launch the old walk, and a failed write costs the next launch the same.
/// Nothing here throws. The key carries how many jars there were and what they
/// weighed together, both of which are cheap to ask, so a pack that gained,
/// lost or changed a single mod simply does not match and is counted again.
/// </summary>
internal static class PackWeightCache
{
    /// <summary>
    /// How many packs are remembered. A player has a handful; the rest are
    /// packs they stopped playing, and their answers are worth nothing.
    /// </summary>
    private const int PacksKept = 24;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>The remembered count, or null for a pack this file cannot answer for.</summary>
    public static int? Recall(string? file, string key)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            lock (Gate)
            {
                var stored = Read(file);
                return stored.TryGetValue(key, out var nested) ? nested : null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the count down, and forgets the packs nobody is playing.</summary>
    public static void Remember(string? file, string key, int nested)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        try
        {
            lock (Gate)
            {
                var stored = Read(file);
                // Newest last, so trimming from the front drops the oldest.
                stored.Remove(key);
                stored[key] = nested;
                while (stored.Count > PacksKept)
                {
                    foreach (var oldest in stored.Keys)
                    {
                        stored.Remove(oldest);
                        break;
                    }
                }
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(stored, JsonOptions));
            }
        }
        catch
        {
            // The next launch counts again, which is what it did before.
        }
    }

    private static Dictionary<string, int> Read(string file)
    {
        if (!File.Exists(file)) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(
                File.ReadAllText(file), JsonOptions);
            return parsed is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
