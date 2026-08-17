using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Turns the pack's own resource packs on in an instance that already exists.
///
/// A pack ships its resource packs in <c>resourcepacks/</c> and names the ones
/// it wants on in <c>launcher/resourcepacks-default.txt</c>. The game only reads
/// its seed options once, when an instance has none, so a player who has already
/// played would get the files and never see them switched on. This switches them
/// on once per version of that list: after that the player owns the choice, and
/// turning one off stays off.
/// </summary>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class ResourcePackDefaultsService(Logger? logger = null)
{
    internal const string ListFileName = "resourcepacks-default.txt";
    internal const string MarkerFileName = ".resourcepacks-applied";
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
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split("  #", 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // "!" marks a pack the game will call outdated; it still plays, but
            // the game keeps such packs in a list of their own.
            var outdated = line.StartsWith('!');
            if (outdated) line = line[1..].Trim();
            if (line.Length == 0) continue;
            entries.Add(line);
            if (outdated) incompatible.Add(line);
        }
        var digest = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(string.Join("\n", entries)))).ToLowerInvariant();
        return new ResourcePackDefaults(digest, entries, incompatible);
    }

    /// <summary>True while this instance has not yet been given this list.</summary>
    public static bool NeedsApplying(string packDirectory, string instanceDirectory)
    {
        var defaults = TryLoad(packDirectory);
        if (defaults is null) return false;
        return ReadMarker(instanceDirectory) != defaults.Value.Sha256;
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
        if (ReadMarker(instanceDirectory) == defaults.Value.Sha256) return 0;

        var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
        var added = 0;
        try
        {
            if (File.Exists(optionsPath))
            {
                var (text, count) = Select(File.ReadAllText(optionsPath, Utf8NoBom), defaults.Value);
                added = count;
                if (count > 0) AtomicFile.WriteAllText(optionsPath, text, Utf8NoBom);
            }
            // An instance without options.txt has not run yet: the game will
            // take the pack's seed options, which already list these packs.
            WriteMarker(instanceDirectory, defaults.Value.Sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The pack's resource packs could not be selected ({ex.Message}): {optionsPath}");
            return 0;
        }

        if (added > 0) logger?.Info($"Selected {added} resource pack(s) of the pack in {optionsPath}.");
        return added;
    }

    /// <summary>
    /// Adds the pack's entries to the two lists the game keeps, leaving the
    /// player's own choices and their order alone.
    /// </summary>
    public static (string Text, int Added) Select(string options, ResourcePackDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        var newline = options.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = options.Length == 0 || options.EndsWith('\n');
        var lines = options.Length == 0
            ? []
            : options.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var added = 0;
        added += Extend(lines, SelectedPrefix, defaults.Entries);
        added += Extend(lines, IncompatiblePrefix, defaults.Incompatible);
        if (added == 0) return (options, 0);

        var text = string.Join(newline, lines);
        if (lines.Count > 0 && endsWithNewline) text += newline;
        return (text, added);
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

    private static string? ReadMarker(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, MarkerFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string instanceDirectory, string sha256)
    {
        Directory.CreateDirectory(instanceDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(instanceDirectory, MarkerFileName),
            sha256 + Environment.NewLine,
            Utf8NoBom);
    }
}
