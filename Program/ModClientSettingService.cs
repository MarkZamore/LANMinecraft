using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Sets one key in one mod's client config inside the instance, once, when the
/// pack asks for it.
///
/// A pack normally says such things by shipping the config file itself, and
/// that works for a fresh install. It does not reach anyone who has already
/// played: the first launch generates the mod's config, and from then on
/// PackInstanceService treats a file the instance has never seen as the
/// player's own - it keeps theirs and puts the pack's copy aside as a conflict.
/// So a setting that has to reach players who are already here needs the same
/// shape as the player-model reset: a token in the pack, a marker in the
/// instance, and one pass that edits the line and never looks again.
///
/// Only the named line is rewritten; every other byte of the file, and the way
/// its lines end, is left as it was. After the pass the value is the player's
/// again - the marker stops the launcher from putting it back.
/// </summary>
/// <param name="logger">Where the summary goes.</param>
public sealed class ModClientSettingService(Logger? logger = null)
{
    /// <summary>
    /// The banner Yes Steve Model draws while it waits for models. On a world's
    /// host the wait never ends - the mod's native side stops answering and the
    /// only code that can clear the state is a callback from it - so the line
    /// sits in the corner for the whole session saying "Loading". The mod has
    /// its own switch for the overlay, and this turns it off.
    /// </summary>
    public static readonly ModClientSetting YsmLoadingBanner = new(
        TokenFileName: "ysm-loading-banner.txt",
        MarkerFileName: ".ysm-loading-banner",
        ConfigRelativePath: "config/yes_steve_model-client.toml",
        Key: "DisableLoadingStateScreen",
        Value: "true",
        Section: "[loading_state_screen]");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What the pack asks for, or null when it asks for nothing.</summary>
    public static string? TryLoadToken(string packDirectory, ModClientSetting setting)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        ArgumentNullException.ThrowIfNull(setting);
        var path = Path.Combine(
            packDirectory,
            PackInstanceService.LauncherDataRoot,
            setting.TokenFileName);
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

    /// <summary>True while this instance has not had the pack's current ask.</summary>
    public static bool NeedsApplying(string packDirectory, string instanceDirectory, ModClientSetting setting)
    {
        var token = TryLoadToken(packDirectory, setting);
        return token is not null && ReadMarker(instanceDirectory, setting) != token;
    }

    /// <summary>
    /// Writes the setting into the instance's config, once. Returns true when
    /// the file changed; false when there was nothing to do, when the mod has
    /// no config here yet, or when the file could not be read.
    /// </summary>
    public bool Apply(string packDirectory, string instanceDirectory, ModClientSetting setting)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        ArgumentNullException.ThrowIfNull(setting);
        var token = TryLoadToken(packDirectory, setting);
        if (token is null || ReadMarker(instanceDirectory, setting) == token) return false;

        var configPath = Path.Combine(
            instanceDirectory,
            setting.ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var changed = false;
        try
        {
            // No config yet means the mod has never run here; the pack's own
            // copy of the file is what a first launch will get, and the marker
            // is not written, so a later launch tries again.
            if (File.Exists(configPath))
            {
                var text = File.ReadAllText(configPath, Utf8NoBom);
                var (rewritten, hit) = SetKey(text, setting.Key, setting.Value);
                if (hit && !string.Equals(rewritten, text, StringComparison.Ordinal))
                {
                    AtomicFile.WriteAllText(configPath, rewritten, Utf8NoBom);
                    changed = true;
                }
                if (hit) WriteMarker(instanceDirectory, setting, token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"The {setting.Key} setting could not be written ({ex.Message}): {configPath}");
            return false;
        }

        if (changed)
        {
            logger?.Info($"Instance setting {setting.Key} set to {setting.Value} in {setting.ConfigRelativePath}.");
        }
        return changed;
    }

    /// <summary>
    /// The same text with <paramref name="key"/> set to <paramref name="value"/>.
    /// Returns the text and whether the key was there to set: a config whose
    /// keys the mod has since renamed is left alone rather than guessed at.
    /// </summary>
    internal static (string Text, bool Found) SetKey(string text, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        var lines = text.Length == 0
            ? new List<string>()
            : text.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var found = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            if (!trimmed.StartsWith(key, StringComparison.Ordinal)) continue;
            var after = trimmed[key.Length..].TrimStart();
            if (!after.StartsWith('=')) continue;

            // The indentation is the file's own - these configs are tab-indented
            // under their table, and a rewritten line has to look like its
            // neighbours or the next diff is noise.
            var indent = line[..(line.Length - trimmed.Length)];
            lines[index] = $"{indent}{key} = {value}";
            found = true;
            break;
        }
        if (!found) return (text, false);

        var rewritten = string.Join(newline, lines);
        if (lines.Count > 0 && endsWithNewline) rewritten += newline;
        return (rewritten, true);
    }

    private static string MarkerPath(string instanceDirectory, ModClientSetting setting) =>
        Path.Combine(instanceDirectory, setting.MarkerFileName);

    private static string? ReadMarker(string instanceDirectory, ModClientSetting setting)
    {
        try
        {
            var path = MarkerPath(instanceDirectory, setting);
            return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string instanceDirectory, ModClientSetting setting, string token)
    {
        try
        {
            AtomicFile.WriteAllText(MarkerPath(instanceDirectory, setting), token, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One key of one mod's client config that a pack may ask for.</summary>
/// <param name="TokenFileName">The pack's ask, under its <c>launcher/</c> folder.</param>
/// <param name="MarkerFileName">What the instance keeps once the ask is done.</param>
/// <param name="ConfigRelativePath">The config file, relative to the instance.</param>
/// <param name="Key">The key to set.</param>
/// <param name="Value">The value to set it to, written verbatim.</param>
/// <param name="Section">The table the key belongs to, for the record.</param>
public sealed record ModClientSetting(
    string TokenFileName,
    string MarkerFileName,
    string ConfigRelativePath,
    string Key,
    string Value,
    string Section);
