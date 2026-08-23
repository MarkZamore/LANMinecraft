using System.IO;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// The files a pack keeps for itself, whatever the instance says.
///
/// An instance is merged from the pack three ways: the pack's old copy, the
/// pack's new copy, and what is on disk. When the third matches neither of the
/// first two the launcher assumes the player edited it and puts the pack's new
/// version aside rather than overwrite somebody's settings. That is right for
/// a config a player tunes and wrong for one the game rewrites on its own -
/// and mod loaders rewrite plenty: NeoForge normalises <c>config/fml.toml</c>
/// at every start, and mods regenerate their own tables from whatever is
/// installed. Such a file looks edited from the first launch onwards, so a fix
/// shipped in the pack could never reach the game that needed it. One
/// dependency line left behind in fml.toml met a player with a red screen on
/// every launch for exactly this reason, twice, while the corrected file sat
/// in the conflicts folder.
///
/// A pack lists those paths in <c>launcher/pack-owned.txt</c> and they are
/// written over. Nothing is lost: what was there is copied into the same
/// conflicts folder first, under its own name.
/// </summary>
public sealed class PackOwnedFileService
{
    internal const string ListFileName = "pack-owned.txt";

    private readonly Regex[] _patterns;

    private PackOwnedFileService(Regex[] patterns) => _patterns = patterns;

    /// <summary>A pack that claims nothing; every file keeps the merge.</summary>
    public static PackOwnedFileService None { get; } = new([]);

    /// <summary>Whether the pack asked to own this path (pack-relative, forward slashes).</summary>
    public bool Owns(string relativePath)
    {
        if (_patterns.Length == 0 || string.IsNullOrWhiteSpace(relativePath)) return false;
        var candidate = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(candidate)) return true;
        }
        return false;
    }

    /// <summary>Reads the list a pack ships, or a service that owns nothing.</summary>
    /// <param name="packDirectory">The pack, not the instance.</param>
    public static PackOwnedFileService Load(string packDirectory, Logger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(packDirectory)) return None;
        var path = Path.Combine(packDirectory, PackInstanceService.LauncherDataRoot, ListFileName);
        string[] lines;
        try
        {
            if (!File.Exists(path)) return None;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The pack's list of its own files could not be read ({ex.Message}); " +
                         "every file keeps the usual merge.");
            return None;
        }

        var patterns = new List<Regex>();
        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            // A pack owning "mods" or the whole tree would turn the merge off
            // wholesale; that is not what this is for.
            if (entry is "*" or "**" or "/" || entry.Contains("..", StringComparison.Ordinal))
            {
                logger?.Warn($"The pack claims '{entry}', which is too broad to honour; ignoring it.");
                continue;
            }
            patterns.Add(Translate(entry));
        }

        if (patterns.Count == 0) return None;
        logger?.Info($"The pack owns {patterns.Count} path pattern(s); those files are written over.");
        return new PackOwnedFileService([.. patterns]);
    }

    /// <summary>
    /// Glob to regex: <c>*</c> stops at a slash, <c>**</c> crosses them, and
    /// <c>?</c> is one character that is not a slash. Everything else is
    /// literal, so a name with a dot or a plus in it means itself.
    /// </summary>
    private static Regex Translate(string glob)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*')
            {
                if (index + 1 < glob.Length && glob[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                }
                else
                {
                    builder.Append("[^/]*");
                }
                continue;
            }
            builder.Append(character == '?' ? "[^/]" : Regex.Escape(character.ToString()));
        }
        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
