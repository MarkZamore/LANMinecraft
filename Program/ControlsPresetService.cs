using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>One key mapping the preset sets: the mapping's name and its options.txt value.</summary>
public readonly record struct ControlsPresetEntry(string Name, string Value);

/// <summary>A pack's controls preset as read from <c>launcher/controls-preset.txt</c>.</summary>
public sealed record ControlsPreset(string Sha256, IReadOnlyList<ControlsPresetEntry> Entries);

/// <summary>Where a build stands against its pack's preset.</summary>
/// <param name="HasPreset">The pack ships a readable preset.</param>
/// <param name="IsApplied">Every line of it already stands in the instance's options.txt.</param>
public readonly record struct ControlsPresetStatus(bool HasPreset, bool IsApplied);

/// <summary>
/// Puts a pack's key layout into a player's game.
///
/// A modpack this size registers hundreds of key mappings and most of them
/// arrive on the same dozen letters. The pack repository keeps a layout that
/// gives every action a key nobody else uses, as
/// <c>launcher/controls-preset.txt</c>: options.txt lines, one per mapping,
/// with comments. Applying it means rewriting those <c>key_</c> lines in the
/// instance's options.txt and touching nothing else there - the game reads the
/// file when it starts, so a layout applied before the game runs is the layout
/// the game runs with.
///
/// The launcher never applies it on its own. The button applies it; while the
/// options file still says what the preset says the button has nothing to do,
/// and the moment the player changes a key in game, or the pack ships a new
/// layout, the button offers again.
/// </summary>
public sealed class ControlsPresetService(Logger? logger = null)
{
    internal const string PresetRelativePath = "launcher/controls-preset.txt";
    internal const string OptionsFileName = "options.txt";
    private const string LinePrefix = "key_";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The preset file of a pack, if the pack has one.</summary>
    internal static string GetPresetPath(string packDirectory) =>
        Path.Combine(packDirectory, PresetRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The pack's own first-launch options, which ConfiguredDefaults would copy in.</summary>
    internal static string GetPackDefaultOptionsPath(string packDirectory) =>
        Path.Combine(packDirectory, "configureddefaults", OptionsFileName);

    /// <summary>
    /// Reads the pack's preset. Null when there is none, or when it cannot be
    /// read as one: a broken preset is no preset, and the log says why.
    /// </summary>
    public ControlsPreset? TryLoad(string packDirectory)
    {
        var path = GetPresetPath(packDirectory);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var entries = Parse(Utf8NoBom.GetString(bytes));
            if (entries.Count == 0)
            {
                logger?.Warn($"The controls preset has no key lines: {path}");
                return null;
            }
            return new ControlsPreset(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            logger?.Warn($"The controls preset could not be read ({ex.Message}): {path}");
            return null;
        }
    }

    /// <summary>
    /// The lines of a preset file. Blank lines and lines starting with <c>#</c>
    /// are comments; a <c>#</c> after two spaces ends a mapping line. Anything
    /// else has to be <c>key_&lt;name&gt;:&lt;value&gt;</c>.
    /// </summary>
    public static IReadOnlyList<ControlsPresetEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<ControlsPresetEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            var comment = line.IndexOf("  #", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var mapping = ParseMappingLine(line);
            if (mapping is null)
            {
                throw new FormatException($"Line {number} is not a key mapping: '{line}'.");
            }
            if (!seen.Add(mapping.Value.Name))
            {
                throw new FormatException($"Line {number} repeats {mapping.Value.Name}.");
            }
            entries.Add(mapping.Value);
        }
        return entries;
    }

    /// <summary>
    /// Compares the instance's options with the pack's preset. No preset, no
    /// options file, or any line that differs: not applied.
    /// </summary>
    public ControlsPresetStatus Evaluate(string packDirectory, string instanceDirectory)
    {
        var preset = TryLoad(packDirectory);
        if (preset is null) return new ControlsPresetStatus(HasPreset: false, IsApplied: false);

        var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
        try
        {
            if (!File.Exists(optionsPath)) return new ControlsPresetStatus(HasPreset: true, IsApplied: false);
            var current = ReadMappings(File.ReadAllText(optionsPath, Utf8NoBom));
            var applied = preset.Entries.All(entry =>
                current.TryGetValue(entry.Name, out var value) &&
                string.Equals(value, entry.Value, StringComparison.Ordinal));
            return new ControlsPresetStatus(HasPreset: true, IsApplied: applied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The instance options could not be read ({ex.Message}): {optionsPath}");
            return new ControlsPresetStatus(HasPreset: true, IsApplied: false);
        }
    }

    /// <summary>
    /// Writes the preset into the instance's options.txt and returns how many
    /// lines it changed or added. Every other line of the file, and the way its
    /// lines end, is left exactly as it was. An instance that has never been
    /// launched has no options.txt yet; then the pack's first-launch defaults
    /// are taken as the starting point - the same file the ConfiguredDefaults
    /// mod would have copied in - so the player loses none of the pack's other
    /// settings to an early preset.
    /// </summary>
    public int Apply(string packDirectory, string instanceDirectory)
    {
        var preset = TryLoad(packDirectory)
            ?? throw new InvalidOperationException("The pack has no controls preset.");
        var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
        string existing;
        if (File.Exists(optionsPath))
        {
            existing = File.ReadAllText(optionsPath, Utf8NoBom);
        }
        else
        {
            var defaults = GetPackDefaultOptionsPath(packDirectory);
            existing = File.Exists(defaults) ? File.ReadAllText(defaults, Utf8NoBom) : string.Empty;
        }

        var (merged, changed) = Merge(existing, preset.Entries);
        if (changed > 0 || !File.Exists(optionsPath))
        {
            AtomicFile.WriteAllText(optionsPath, merged, Utf8NoBom);
        }
        logger?.Info($"Controls preset {preset.Sha256[..12]} applied to {optionsPath}: {changed} line(s) changed.");
        return changed;
    }

    /// <summary>
    /// The options text with the preset's mappings in place. Lines the preset
    /// does not mention are untouched; mappings the file lacks are appended
    /// after the last <c>key_</c> line, or at the end. Returns the new text and
    /// the count of lines changed or added.
    /// </summary>
    public static (string Text, int Changed) Merge(string options, IReadOnlyList<ControlsPresetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entries);
        var newline = options.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = options.Length == 0 || options.EndsWith('\n');
        var lines = options.Length == 0
            ? new List<string>()
            : options.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries) wanted[entry.Name] = entry.Value;

        var changed = 0;
        var lastKeyLine = -1;
        // A file may already carry the same mapping twice, and one repeated
        // key_ line is fatal: NeoForge reads these into a map and stops before
        // its loading window with "Duplicate key ... attempted merging values".
        // The game never shows that file to anyone, so this is the only place
        // it can be healed - and it heals every mapping, not only ours.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var mapping = ParseMappingLine(lines[index]);
            if (mapping is null) continue;
            if (!seen.Add(mapping.Value.Name))
            {
                lines.RemoveAt(index--);
                changed++;
                continue;
            }
            lastKeyLine = index;
            if (!wanted.TryGetValue(mapping.Value.Name, out var value)) continue;
            wanted.Remove(mapping.Value.Name);
            if (string.Equals(mapping.Value.Value, value, StringComparison.Ordinal)) continue;
            lines[index] = LinePrefix + mapping.Value.Name + ":" + value;
            changed++;
        }

        // What the file did not know goes where the game itself keeps the keys.
        var insertAt = lastKeyLine >= 0 ? lastKeyLine + 1 : lines.Count;
        foreach (var entry in entries)
        {
            if (!wanted.ContainsKey(entry.Name)) continue;
            lines.Insert(insertAt++, LinePrefix + entry.Name + ":" + entry.Value);
            changed++;
        }

        var text = string.Join(newline, lines);
        if (lines.Count > 0 && endsWithNewline) text += newline;
        return (text, changed);
    }

    /// <summary>
    /// Drops repeated <c>key_</c> lines from an instance's options, whoever
    /// wrote them, and says how many went.
    ///
    /// One repeated line stops NeoForge in DisplayWindow.initialize with
    /// "Duplicate key ... attempted merging values" - before the loading window,
    /// before any mod, with nothing in the game's own log to show for it. The
    /// game writes this file itself and a mod's key can end up in it twice, so
    /// this runs before every launch rather than only when the preset is
    /// applied: a player whose file is already broken never gets to press that
    /// button, because the preset counts as applied and the button is off.
    /// </summary>
    public int RemoveDuplicateMappings(string instanceDirectory)
    {
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
        try
        {
            if (!File.Exists(optionsPath)) return 0;
            var (text, removed) = WithoutDuplicateMappings(File.ReadAllText(optionsPath, Utf8NoBom));
            if (removed == 0) return 0;
            AtomicFile.WriteAllText(optionsPath, text, Utf8NoBom);
            logger?.Info($"Removed {removed} repeated key line(s) from {optionsPath}; that pair stops the game before its loading window.");
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The instance options could not be repaired ({ex.Message}): {optionsPath}");
            return 0;
        }
    }

    /// <summary>The same options text with only the first line of each mapping.</summary>
    public static (string Text, int Removed) WithoutDuplicateMappings(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var newline = options.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = options.Length == 0 || options.EndsWith('\n');
        var lines = options.Length == 0
            ? []
            : options.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var mapping = ParseMappingLine(lines[index]);
            if (mapping is null) continue;
            if (seen.Add(mapping.Value.Name)) continue;
            lines.RemoveAt(index--);
            removed++;
        }
        if (removed == 0) return (options, 0);

        var text = string.Join(newline, lines);
        if (lines.Count > 0 && endsWithNewline) text += newline;
        return (text, removed);
    }

    /// <summary>The key mappings an options file holds, by name.</summary>
    internal static Dictionary<string, string> ReadMappings(string options)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in options.Split('\n'))
        {
            var mapping = ParseMappingLine(raw.TrimEnd('\r'));
            if (mapping is not null) mappings[mapping.Value.Name] = mapping.Value.Value;
        }
        return mappings;
    }

    private static ControlsPresetEntry? ParseMappingLine(string line)
    {
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal)) return null;
        var separator = line.IndexOf(':', LinePrefix.Length);
        if (separator <= LinePrefix.Length || separator == line.Length - 1) return null;
        var name = line[LinePrefix.Length..separator];
        var value = line[(separator + 1)..];
        // A mapping name is a translation key, but a mod that registered its
        // mappings by display name leaves "key_Speed Module:..." in the file, so
        // spaces inside a name are real; a value never has any.
        if (name.Trim() != name || value.Any(char.IsWhiteSpace)) return null;
        return new ControlsPresetEntry(name, value);
    }
}
