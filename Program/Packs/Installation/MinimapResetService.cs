using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Throws away the map a player's minimap has already drawn, once, when the
/// pack asks for it.
///
/// A minimap keeps what it saw as tiles on disk. Reset the world's chunks and
/// those tiles become a picture of a place that no longer exists: the map
/// shows yesterday's terrain over ground the game has since generated anew,
/// and nothing in the mod ever notices. The pack names the reset in
/// <c>launcher/map-reset.txt</c>; every instance performs it once per version
/// of that file, so all the players get the same clean slate without anybody
/// deleting folders by hand. Waypoints and settings are not touched - only the
/// drawn tiles, which the game redraws by walking.
/// </summary>
/// <param name="logger">Where the one-line summary goes.</param>
public sealed class MinimapResetService(Logger? logger = null)
{
    internal const string TokenFileName = "map-reset.txt";
    internal const string MarkerFileName = ".minimap-reset";

    /// <summary>The caches a minimap redraws by itself, relative to the instance.</summary>
    internal static readonly IReadOnlyList<string> DrawnCaches =
    [
        Path.Combine("xaero", "world-map"),
        Path.Combine("journeymap", "data"),
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The reset the pack asks for, or null when it asks for none.</summary>
    public static string? TryLoadToken(string packDirectory)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        var path = Path.Combine(packDirectory, PackInstanceService.LauncherDataRoot, TokenFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Utf8NoBom);
            return Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(text.Trim()))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True while this instance has not performed the pack's current reset.</summary>
    public static bool NeedsApplying(string packDirectory, string instanceDirectory)
    {
        var token = TryLoadToken(packDirectory);
        return token is not null && ReadMarker(instanceDirectory) != token;
    }

    /// <summary>
    /// Deletes the drawn tiles once. Returns how many caches were removed;
    /// zero when there was nothing to do or nothing drawn yet.
    /// </summary>
    public int Apply(string packDirectory, string instanceDirectory)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        var token = TryLoadToken(packDirectory);
        if (token is null || ReadMarker(instanceDirectory) == token) return 0;

        var removed = 0;
        foreach (var cache in DrawnCaches)
        {
            var path = Path.Combine(instanceDirectory, cache);
            if (!Directory.Exists(path)) continue;
            try
            {
                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"The drawn map could not be cleared ({ex.Message}): {path}");
                // Written down all the same: a cache that cannot be deleted now
                // would otherwise be attempted on every launch forever.
            }
        }

        WriteMarker(instanceDirectory, token);
        if (removed > 0) logger?.Info($"Cleared {removed} drawn map cache(s) in {instanceDirectory}; the minimap draws them again.");
        return removed;
    }

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

    private static void WriteMarker(string instanceDirectory, string token)
    {
        Directory.CreateDirectory(instanceDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(instanceDirectory, MarkerFileName),
            token + Environment.NewLine,
            Utf8NoBom);
    }
}
