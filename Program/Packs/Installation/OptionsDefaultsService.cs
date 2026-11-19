using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Seeds Russian in the game's options and applies the pack's option defaults.
/// </summary>
/// <remarks>
/// In <c>launcher/options-default.txt</c>, plain entries fill missing keys.
/// Entries marked with <c>!</c> are reapplied at each Java launch. Language
/// always belongs to the player once an instance has a saved choice.
/// </remarks>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class OptionsDefaultsService(Logger? logger = null)
{
    internal const string ListFileName = "options-default.txt";
    private const string OptionsFileName = "options.txt";
    private const string LanguageKey = "lang";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One setting a pack asks for, and whether it is the pack's to keep.</summary>
    /// <param name="Key">The option's name in options.txt.</param>
    /// <param name="Value">What the pack wants it set to.</param>
    /// <param name="Held">Marked with <c>!</c>: put back at every launch rather than seeded once.</param>
    public readonly record struct OptionDefault(string Key, string Value, bool Held);

    /// <summary>The settings a pack asks for, in file order.</summary>
    public static IReadOnlyList<OptionDefault> Parse(string text)
    {
        var wanted = new List<OptionDefault>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var held = line.StartsWith('!');
            if (held) line = line[1..].TrimStart();

            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            // A key named twice is a mistake in the pack, not an instruction to
            // write it twice; the first one stands.
            if (key.Length == 0 || !seen.Add(key)) continue;
            wanted.Add(new OptionDefault(key, value, held));
        }
        return wanted;
    }

    /// <summary>Reads the pack's list, or null when it ships none.</summary>
    public static IReadOnlyList<OptionDefault>? TryLoad(string packDirectory)
    {
        try
        {
            var path = Path.Combine(packDirectory, PackInstanceService.LauncherDataRoot, ListFileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prepares game options and returns how many requested values changed.
    /// </summary>
    public int Apply(string packDirectory, string instanceDirectory, string minecraftVersion, bool native = false,
        string? clientJar = null)
    {
        if (string.IsNullOrWhiteSpace(packDirectory) || string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return 0;
        }

        try
        {
            var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
            var options = File.Exists(optionsPath) ? File.ReadAllText(optionsPath) : null;
            var source = options ?? ReadAuthoredOptions(instanceDirectory, native);
            var lines = ReadLines(source).ToList();
            if (options is null) lines.RemoveAll(IsLanguage);
            var wanted = new List<OptionDefault> { new(LanguageKey, RussianLanguage(minecraftVersion), Held: false) };
            if (!native && TryLoad(packDirectory) is { } packOptions)
            {
                wanted.AddRange(packOptions.Where(option => option.Key != LanguageKey));
            }
            var whereItIs = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < lines.Count; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator > 0) whereItIs.TryAdd(lines[index][..separator].Trim(), index);
            }

            var added = 0;
            var held = 0;
            foreach (var (key, value, isHeld) in wanted)
            {
                var line = $"{key}:{value}";
                if (!whereItIs.TryGetValue(key, out var index))
                {
                    whereItIs[key] = lines.Count;
                    lines.Add(line);
                    added++;
                    continue;
                }
                // A setting the pack keeps is put back where it already stands,
                // so the file's own order survives; one it merely seeded is left
                // exactly as the player left it.
                if (lines[index] == line) continue;
                if (!isHeld && (key != LanguageKey || !lines[index].TrimEnd().EndsWith(':'))) continue;
                lines[index] = line;
                held++;
            }

            if (added == 0 && held == 0) return 0;
            // A sparse, unversioned file runs through the old game migrations.
            // Keep fresh native defaults by marking it with the prepared client's version.
            if (options is null && !whereItIs.ContainsKey("version") && ReadDataVersion(clientJar) is { } dataVersion)
                lines.Insert(0, FormattableString.Invariant($"version:{dataVersion}"));

            Directory.CreateDirectory(instanceDirectory);
            var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            AtomicFile.WriteAllText(optionsPath, string.Join(newline, lines) + newline, Utf8NoBom);
            logger?.Info($"Game options prepared: {added} defaults added, {held} values updated.");
            return added + held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Game options could not be written: {ex.Message}");
            return 0;
        }
    }

    internal static string PrepareInitialPresetOptions(string packDirectory, string minecraftVersion, bool native)
    {
        var source = ReadAuthoredOptions(packDirectory, native);
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = ReadLines(source).Where(line => !IsLanguage(line));
        return string.Join(newline, lines.Append(LanguageKey + ":" + RussianLanguage(minecraftVersion))) + newline;
    }

    private static string ReadAuthoredOptions(string directory, bool native)
    {
        var configuredDefaults = ControlsPresetService.GetPackDefaultOptionsPath(directory);
        if (native) return File.Exists(configuredDefaults) ? File.ReadAllText(configuredDefaults) : "";

        // YOSBR copies only absent files before the option loaders run.
        var yosbr = Path.Combine(directory, "config", "yosbr", OptionsFileName);
        if (File.Exists(yosbr)) return File.ReadAllText(yosbr);
        if (File.Exists(configuredDefaults)) return File.ReadAllText(configuredDefaults);

        // Default Options seeds this file only while options.txt is absent and
        // supplies key mappings separately, including on the old Forge loader.
        var defaultOptions = Path.Combine(directory, "config", "defaultoptions", OptionsFileName);
        if (!File.Exists(defaultOptions)) return "";
        var source = File.ReadAllText(defaultOptions);
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(newline, ReadLines(source).Where(line => !line.StartsWith("key_", StringComparison.Ordinal)));
    }

    private static string[] ReadLines(string text) => text.Length == 0
        ? []
        : text.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    private static bool IsLanguage(string line)
    {
        var separator = line.IndexOf(':');
        return separator > 0 && line.AsSpan(0, separator).Trim().SequenceEqual(LanguageKey);
    }

    private static string RussianLanguage(string minecraftVersion)
    {
        // Before 1.11, Minecraft identifies the Russian locale with ru_RU.
        var suffix = minecraftVersion.IndexOf('-');
        var release = suffix < 0 ? minecraftVersion : minecraftVersion[..suffix];
        return Version.TryParse(release, out var version) && version < new Version(1, 11)
            ? "ru_RU"
            : "ru_ru";
    }

    private static int? ReadDataVersion(string? clientJar)
    {
        if (string.IsNullOrEmpty(clientJar)) return null;
        try
        {
            using var archive = ZipFile.OpenRead(clientJar);
            var entry = archive.GetEntry("version.json");
            if (entry is null || entry.Length > 64 * 1024) return null;
            using var input = entry.Open();
            using var doc = JsonDocument.Parse(input);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("world_version", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var version) && version > 0 ? version : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
