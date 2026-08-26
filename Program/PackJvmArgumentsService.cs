using System.IO;

namespace Minecraft;

/// <summary>
/// The Java options a pack asks to be started with, from
/// <c>launcher/jvm-args.txt</c>.
/// </summary>
/// <remarks>
/// A pack can ship mods and their configs, but some of what a mod needs is not
/// in a config file at all: ModernFix reads its most valuable option out of a
/// JVM property, because it rewrites its own config on every launch and a copy
/// shipped in the pack could never survive to be read. So the launcher used to
/// pass that one property to every pack it started.
///
/// Which was fine while every pack it started was NeoForge 1.21.1, and became
/// wrong the moment it was not. That option rewrites model loading wholesale,
/// and on older versions model loading is where half the pack already lives:
/// Continuity refuses to run beside it before 1.19.4 and says so on a screen
/// instead of starting, and BetterEnd on 1.18.2 fails to apply at all -
///
///     Mixin apply for mod betterend failed ... net.minecraft.class_1088:
///     Variable modifier target for be_switchModel was removed by another injector
///
/// - because ModernFix had already taken the variable it modifies. Neither is a
/// bug in anybody's mod. They are what happens when one program decides how
/// another program's mods should be configured.
///
/// So it is the pack's decision, in the pack, beside the other things a pack
/// decides for itself. One option per line, blank lines and <c>#</c> comments
/// ignored. A line that is not a Java option is refused rather than passed on:
/// the JVM does not start at all on an argument it does not understand, and a
/// pack that cannot start is worse than a pack missing an optimisation.
/// </remarks>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class PackJvmArgumentsService(Logger? logger = null)
{
    internal const string FileName = "jvm-args.txt";

    /// <summary>The options a pack asks for, in file order.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // A Java option and nothing else. Everything the JVM accepts starts
            // with a dash, and anything else on this line is either a mistake or
            // somebody trying to make the launcher run a different program.
            if (!line.StartsWith('-')) continue;
            if (!seen.Add(line)) continue;
            wanted.Add(line);
        }
        return wanted;
    }

    /// <summary>Reads the pack's list, or an empty one when it asks for nothing.</summary>
    public IReadOnlyList<string> Load(string packDirectory)
    {
        if (string.IsNullOrWhiteSpace(packDirectory)) return [];
        try
        {
            var path = Path.Combine(packDirectory, PackInstanceService.LauncherDataRoot, FileName);
            if (!File.Exists(path)) return [];

            var text = File.ReadAllText(path);
            var wanted = Parse(text);
            var refused = text.Split('\n')
                .Select(line => line.Trim())
                .Count(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith('-'));
            if (refused > 0)
            {
                logger?.Warn(
                    $"{refused} line(s) in the pack's {FileName} are not Java options and were left out; " +
                    "every line must begin with a dash.");
            }
            if (wanted.Count > 0)
            {
                logger?.Info($"The pack asks to be started with: {string.Join(' ', wanted)}");
            }
            return wanted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The pack's {FileName} could not be read: {ex.Message}");
            return [];
        }
    }
}
