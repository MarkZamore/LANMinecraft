using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Keeps the pack's own resource packs selected in an instance that already
/// exists - and only the pack's own.
///
/// A pack ships its resource packs in <c>resourcepacks/</c> and names the ones
/// it wants on in <c>launcher/resourcepacks-default.txt</c>. The game only reads
/// its seed options once, when an instance has none, so a player who has already
/// played would get the files and never see them switched on. This applies the
/// list once per version of it: the pack's packs go on, in the pack's order,
/// and what the pack has since dropped comes off - the file is gone from the
/// instance by then, and a selection naming a pack that is not there is a
/// warning at every start. After that the player owns the choice. Packs the
/// player added themselves are never in the pack's list, so they are never
/// touched: not reordered, not switched off.
/// </summary>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class ResourcePackDefaultsService(Logger? logger = null)
{
    internal const string ListFileName = "resourcepacks-default.txt";
    internal const string MarkerFileName = ".resourcepacks-applied";
    private const string MarkerListPrefix = "pack:";
    private const string OptionsFileName = "options.txt";
    private const string SelectedPrefix = "resourcePacks:";
    private const string IncompatiblePrefix = "incompatibleResourcePacks:";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What a pack asks for: the entries to select, newest priority last.</summary>
    /// <param name="Sha256">Identity of the list, so a changed list applies again.</param>
    /// <param name="Entries">Options entries such as <c>file/Some Pack.zip</c>.</param>
    /// <param name="Incompatible">Those built for an older game, which the game lists apart.</param>
    public readonly record struct ResourcePackDefaults(string Sha256, IReadOnlyList<string> Entries, IReadOnlyList<string> Incompatible);

    /// <summary>Reads the list a pack ships, or null when it ships none.</summary>
    public static ResourcePackDefaults? TryLoad(string packDirectory)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        var path = Path.Combine(packDirectory, PackInstanceService.LauncherDataRoot, ListFileName);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Utf8NoBom);
        return Parse(text);
    }

    /// <summary>The list as the file writes it: one entry per line, <c>#</c> comments ignored.</summary>
    public static ResourcePackDefaults Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<string>();
        var incompatible = new List<string>();
        var marked = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split("  #", 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // "!" marks a pack the game will call outdated; it still plays, but
            // the game keeps such packs in a list of their own. Use it only for
            // a pack the game truly refuses: told a pack is incompatible and
            // then finding it compatible, the game takes it off the selection.
            var outdated = line.StartsWith('!');
            if (outdated) line = line[1..].Trim();
            if (line.Length == 0) continue;
            entries.Add(line);
            marked.Add(outdated ? "!" + line : line);
            if (outdated) incompatible.Add(line);
        }
        // The marks are part of the list's identity: taking one off changes
        // nothing about which packs are named and everything about whether the
        // game keeps them selected, so it has to count as a new list.
        var digest = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(string.Join("\n", marked)))).ToLowerInvariant();
        return new ResourcePackDefaults(digest, entries, incompatible);
    }

    /// <summary>True while this instance has not yet been given this list.</summary>
    public static bool NeedsApplying(string packDirectory, string instanceDirectory)
    {
        var defaults = TryLoad(packDirectory);
        if (defaults is null) return false;
        return ReadMarker(instanceDirectory).Sha256 != defaults.Value.Sha256;
    }

    /// <summary>
    /// Selects the pack's resource packs in the instance's options, once.
    /// Returns how many entries were added; zero when there was nothing to do.
    /// </summary>
    public int Apply(string packDirectory, string instanceDirectory)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        var defaults = TryLoad(packDirectory);
        if (defaults is null) return 0;
        var marker = ReadMarker(instanceDirectory);
        if (marker.Sha256 == defaults.Value.Sha256) return 0;

        var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
        var added = 0;
        try
        {
            if (File.Exists(optionsPath))
            {
                var options = File.ReadAllText(optionsPath, Utf8NoBom);
                // What the pack listed last time and lists no longer is the
                // pack's to take back; the player's own packs were never listed.
                var dropped = marker.Entries
                    .Where(entry => !defaults.Value.Entries.Contains(entry, StringComparer.Ordinal))
                    .ToList();
                var (text, count) = Select(options, defaults.Value, dropped);
                added = count;
                // Written on any difference, not only on a new entry: giving the
                // pack's own packs the order the pack declares changes nothing
                // about which are selected and everything about which wins.
                if (!string.Equals(text, options, StringComparison.Ordinal))
                {
                    AtomicFile.WriteAllText(optionsPath, text, Utf8NoBom);
                }
            }
            // An instance without options.txt has not run yet: the game will
            // take the pack's seed options, which already list these packs.
            WriteMarker(instanceDirectory, defaults.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The pack's resource packs could not be selected ({ex.Message}): {optionsPath}");
            return 0;
        }

        logger?.Info($"Applied the pack's resource packs in {optionsPath}: {added} newly selected.");
        return added;
    }

    /// <summary>
    /// Adds the pack's entries to the two lists the game keeps, leaving the
    /// player's own choices and their order alone, and takes out the entries in
    /// <paramref name="dropped"/> - what the pack used to list and no longer does.
    /// </summary>
    public static (string Text, int Added) Select(string options, ResourcePackDefaults defaults, IReadOnlyList<string>? dropped = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        dropped ??= [];
        var newline = options.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = options.Length == 0 || options.EndsWith('\n');
        var lines = options.Length == 0
            ? []
            : options.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var (added, rearranged) = Arrange(lines, SelectedPrefix, defaults.Entries, dropped);
        // The second list is bookkeeping the game keeps about packs built for an
        // older version, so it counts towards rewriting the file and not towards
        // the number of packs the player just gained.
        var incompatible = Extend(lines, IncompatiblePrefix, defaults.Incompatible);
        // Everything the pack owns and no longer calls incompatible comes out of
        // that list. A pack sitting there that the game then judges compatible
        // is one the game quietly deselects, so a stale mark is not a harmless
        // leftover - it is the reason a pack the player was given goes dark.
        var forgotten = Remove(
            lines,
            IncompatiblePrefix,
            defaults.Entries
                .Where(entry => !defaults.Incompatible.Contains(entry, StringComparer.Ordinal))
                .Concat(dropped)
                .ToList());
        if (!rearranged && incompatible == 0 && !forgotten) return (options, 0);

        var text = string.Join(newline, lines);
        if (lines.Count > 0 && endsWithNewline) text += newline;
        return (text, added);
    }

    /// <summary>
    /// Lays the pack's own entries out in the order the pack declares, above
    /// everything else the player has selected. Anything that is not the pack's
    /// keeps its place and its order, and the player's later changes are not
    /// touched again: the list is applied once per version of itself.
    /// </summary>
    private static (int Added, bool Rearranged) Arrange(List<string> lines, string prefix, IReadOnlyList<string> wanted, IReadOnlyList<string> dropped)
    {
        if (wanted.Count == 0 && dropped.Count == 0) return (0, false);
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var current = index >= 0 ? ReadList(lines[index][prefix.Length..]) : [];
        var mine = new HashSet<string>(wanted.Concat(dropped), StringComparer.Ordinal);
        var arranged = current.Where(entry => !mine.Contains(entry)).Concat(wanted).ToList();
        if (arranged.SequenceEqual(current, StringComparer.Ordinal)) return (0, false);

        var added = wanted.Count(entry => !current.Contains(entry, StringComparer.Ordinal));
        var line = prefix + WriteList(arranged);
        if (index >= 0) lines[index] = line; else lines.Add(line);
        return (added, true);
    }

    /// <summary>Takes entries out of one of the game's lists; true when the line changed.</summary>
    private static bool Remove(List<string> lines, string prefix, IReadOnlyList<string> unwanted)
    {
        if (unwanted.Count == 0) return false;
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (index < 0) return false;
        var current = ReadList(lines[index][prefix.Length..]);
        var kept = current.Where(entry => !unwanted.Contains(entry, StringComparer.Ordinal)).ToList();
        if (kept.Count == current.Count) return false;
        lines[index] = prefix + WriteList(kept);
        return true;
    }

    private static int Extend(List<string> lines, string prefix, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return 0;
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var current = index >= 0 ? ReadList(lines[index][prefix.Length..]) : [];
        var added = 0;
        foreach (var entry in wanted)
        {
            if (current.Contains(entry, StringComparer.Ordinal)) continue;
            current.Add(entry);
            added++;
        }
        if (added == 0) return 0;
        var line = prefix + WriteList(current);
        if (index >= 0) lines[index] = line; else lines.Add(line);
        return added;
    }

    /// <summary>The game writes these as a JSON array of quoted strings on one line.</summary>
    internal static List<string> ReadList(string value)
    {
        var items = new List<string>();
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']') return items;
        var body = trimmed[1..^1];
        var quoted = false;
        var current = new StringBuilder();
        for (var index = 0; index < body.Length; index++)
        {
            var symbol = body[index];
            if (symbol == '"' && (index == 0 || body[index - 1] != '\\'))
            {
                if (quoted) items.Add(current.ToString());
                current.Clear();
                quoted = !quoted;
                continue;
            }
            if (quoted) current.Append(symbol);
        }
        return items;
    }

    internal static string WriteList(IReadOnlyList<string> items) =>
        "[" + string.Join(",", items.Select(item => "\"" + item.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) + "]";

    /// <summary>
    /// What was applied last time: the list's identity on the first line, then
    /// the entries it held, so the next list knows what to take back. A marker
    /// from before entries were recorded gives its identity and nothing else,
    /// which only means the pack takes back nothing that once.
    /// </summary>
    private static (string? Sha256, IReadOnlyList<string> Entries) ReadMarker(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, MarkerFileName);
        try
        {
            if (!File.Exists(path)) return (null, []);
            var lines = File.ReadAllText(path, Utf8NoBom).Split('\n').Select(line => line.Trim()).ToList();
            var sha = lines.FirstOrDefault(line => line.Length > 0);
            var entries = lines
                .Where(line => line.StartsWith(MarkerListPrefix, StringComparison.Ordinal))
                .Select(line => line[MarkerListPrefix.Length..])
                .Where(entry => entry.Length > 0)
                .ToList();
            return (sha, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, []);
        }
    }

    private static void WriteMarker(string instanceDirectory, ResourcePackDefaults defaults)
    {
        Directory.CreateDirectory(instanceDirectory);
        var text = new StringBuilder().Append(defaults.Sha256).Append('\n');
        foreach (var entry in defaults.Entries) text.Append(MarkerListPrefix).Append(entry).Append('\n');
        AtomicFile.WriteAllText(Path.Combine(instanceDirectory, MarkerFileName), text.ToString(), Utf8NoBom);
    }
}
