using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Puts every player of a world back on the plain Steve model of Yes Steve
/// Model, once, when the pack asks for it.
///
/// The mod keeps a player's chosen model not in a config file but on the
/// player themselves - a NeoForge attachment inside <c>playerdata/*.dat</c>
/// (and inside the <c>Player</c> compound of <c>level.dat</c> for the world's
/// owner). So a pack cannot change the choice by shipping a file; it names
/// the reset in <c>launcher/player-model-reset.txt</c>, and every launcher
/// performs it once per version of that file, on every profile of every world
/// it prepares. After that the choice is the player's again: a model picked
/// later stays picked.
/// </summary>
/// <param name="logger">Where the summary goes.</param>
public sealed class PlayerModelResetService(Logger? logger = null)
{
    internal const string TokenFileName = "player-model-reset.txt";
    internal const string MarkerFileName = ".player-model-reset";
    internal const string AttachmentsName = "neoforge:attachments";
    internal const string ModelAttachmentName = "yes_steve_model:model_id";
    /// <summary>
    /// What the mod writes for a player who has chosen no model of its own: the
    /// game draws them itself, in the ordinary Minecraft body and their own
    /// skin. Not "default" - that is the name of one of the mod's own models,
    /// a shorter figure with its own texture, and setting it gave every player
    /// that model instead of themselves.
    /// </summary>
    internal const string DefaultModelId = "disabled";

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

    /// <summary>True while this world has not had the pack's current reset.</summary>
    public static bool NeedsApplying(string packDirectory, string worldPath)
    {
        var token = TryLoadToken(packDirectory);
        return token is not null && ReadMarker(worldPath) != token;
    }

    /// <summary>
    /// Sets the model of every player in the world to the default, once.
    /// Returns how many players were changed; zero when nothing was to be done.
    /// </summary>
    public int Apply(string packDirectory, string worldPath)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);
        ArgumentNullException.ThrowIfNull(worldPath);
        var token = TryLoadToken(packDirectory);
        if (token is null || ReadMarker(worldPath) == token) return 0;

        var changed = 0;
        var playerData = Path.Combine(worldPath, "playerdata");
        if (Directory.Exists(playerData))
        {
            foreach (var file in Directory.EnumerateFiles(playerData, "*.dat"))
            {
                changed += ResetFile(file, root => root) ? 1 : 0;
            }
        }
        var level = Path.Combine(worldPath, "level.dat");
        if (File.Exists(level))
        {
            changed += ResetFile(level, root => root.GetCompound("Data")?.GetCompound("Player")) ? 1 : 0;
        }

        WriteMarker(worldPath, token);
        if (changed > 0)
        {
            logger?.Info($"World {Path.GetFileName(worldPath)}: {changed} player model(s) put back on the default Steve.");
        }
        return changed;
    }

    /// <summary>
    /// Changes the model in one file when it names another one. Everything else
    /// in the file is written back exactly as it was read.
    /// </summary>
    internal static bool ResetModel(NbtCompoundTag player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var attachments = player.GetCompound(AttachmentsName);
        var model = attachments?.GetCompound(ModelAttachmentName);
        if (model is null) return false;
        if (string.Equals(model.GetString("model_id"), DefaultModelId, StringComparison.Ordinal)) return false;
        model.Set("model_id", new NbtStringTag(DefaultModelId));
        // No model, no texture chosen inside one: the skin the player wears is
        // the one the game already has for them.
        model.Set("select_texture", new NbtStringTag(string.Empty));
        return true;
    }

    private bool ResetFile(string path, Func<NbtCompoundTag, NbtCompoundTag?> locatePlayer)
    {
        try
        {
            var file = NbtFile.Read(path);
            var player = locatePlayer(file.Root);
            if (player is null || !ResetModel(player)) return false;
            file.Write(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger?.Warn($"The player model in {path} could not be reset ({ex.Message}).");
            return false;
        }
    }

    private static string? ReadMarker(string worldPath)
    {
        var path = Path.Combine(worldPath, MarkerFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string worldPath, string token)
    {
        Directory.CreateDirectory(worldPath);
        AtomicFile.WriteAllText(Path.Combine(worldPath, MarkerFileName), token + Environment.NewLine, Utf8NoBom);
    }
}
