using System.IO;
using System.Text;

namespace Minecraft;

/// <summary>
/// Gives a pack the game settings it wants to be met with, once.
///
/// A pack can ship mods and their configs, and it can ship a controls preset,
/// but the things that decide whether a small machine can play it at all -
/// render distance, simulation distance, graphics quality - live in
/// <c>options.txt</c>, which belongs to the launcher and to the player. So a
/// pack that is built for a weak laptop had no way to arrive that way: it
/// arrived at the vanilla defaults, twelve chunks of render distance and fancy
/// graphics, and the player found out by watching it stutter.
///
/// A pack names those settings in <c>launcher/options-default.txt</c>, in the
/// game's own <c>key:value</c> form, and they are written into an instance that
/// does not already have them.
/// </summary>
/// <remarks>
/// Only keys the file does not already hold are written, and that single rule
/// is what makes this safe to run at every launch with no marker to keep and no
/// state to get wrong. The game writes back every option it knows the first
/// time it saves, so after one session every key exists and nothing here can
/// ever change one again. What the player set stays set - including the very
/// settings this seeded, the moment they touch them.
///
/// One mark makes room for the other kind of setting. A line beginning with
/// <c>!</c> is the pack's rather than the player's, and it is put back at every
/// launch whatever the file says - the way the resource pack list is. It is for
/// the settings a build depends on looking the same for everybody: a value
/// somebody can change in a screen and then wonder why the game does not match
/// what they were told. Everything without the mark is a starting point and
/// nothing more, which is what nearly every setting should be.
/// </remarks>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class OptionsDefaultsService(Logger? logger = null)
{
    internal const string ListFileName = "options-default.txt";
    private const string OptionsFileName = "options.txt";
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
    /// Writes the settings this instance does not have yet. Returns how many
    /// were added.
    /// </summary>
    public int Apply(string packDirectory, string instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(packDirectory) || string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return 0;
        }

        try
        {
            var wanted = TryLoad(packDirectory);
            if (wanted is null || wanted.Count == 0) return 0;

            var optionsPath = Path.Combine(instanceDirectory, OptionsFileName);
            var lines = File.Exists(optionsPath)
                ? new List<string>(File.ReadAllLines(optionsPath))
                : [];
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
                if (!isHeld || lines[index] == line) continue;
                lines[index] = line;
                held++;
            }

            if (added == 0 && held == 0) return 0;

            Directory.CreateDirectory(instanceDirectory);
            AtomicFile.WriteAllText(optionsPath, string.Join('\n', lines) + '\n', Utf8NoBom);
            logger?.Info(
                added > 0 && held > 0
                    ? $"The pack's own game settings: {added} written into a new instance, {held} put back."
                    : added > 0
                        ? $"The pack's own game settings were written into a new instance: {added} of them."
                        : $"The pack keeps {held} game setting(s), and they had been changed; they are back.");
            return added + held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The pack's game settings could not be written: {ex.Message}");
            return 0;
        }
    }
}
