using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Puts every player of a world back on the ordinary Minecraft model - their
/// own body, their own skin - by switching Yes Steve Model off for them, once,
/// when the pack asks for it.
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
    /// The switch that turns the mod off for one player: with it set the game
    /// draws them itself, in the ordinary Minecraft body and their own skin.
    ///
    /// It took two wrong turns to find. The mod's <c>model_id</c> is only the
    /// name of a model, and "default" is the name of one of the mod's own -
    /// a shorter figure with its own texture, which is what every player got.
    /// Writing "disabled" as the id was no better: no model answers to that
    /// name, so the mod fell back to its own default and saved it over us on
    /// the next join. The mod keeps the real answer beside the id, as a
    /// boolean, and this is it.
    /// </summary>
    internal const string DisabledFlagName = "disabled";
    /// <summary>The mod's own name for its own model; a valid id to leave behind.</summary>
    internal const string DefaultModelId = "default";

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
    /// Turns the mod off for every player in the world, once.
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
            logger?.Info($"World {Path.GetFileName(worldPath)}: {changed} player(s) put back on the ordinary Minecraft model.");
        }
        return changed;
    }

    /// <summary>
    /// Turns the mod off for one player. Everything else in the file is
    /// written back exactly as it was read.
    /// </summary>
    internal static bool ResetModel(NbtCompoundTag player)
    {
        ArgumentNullException.ThrowIfNull(player);
        // A player who has never opened the mod carries no attachment at all,
        // and the mod gives them its own model the first time they join. So the
        // switch is written for them too, not only for those who already chose.
        var attachments = player.GetCompound(AttachmentsName);
        if (attachments is null)
        {
            attachments = new NbtCompoundTag();
            player.Set(AttachmentsName, attachments);
        }
        var model = attachments.GetCompound(ModelAttachmentName);
        if (model is null)
        {
            model = new NbtCompoundTag();
            attachments.Set(ModelAttachmentName, model);
        }
        else if (model.GetByte(DisabledFlagName) == 1)
        {
            return false;
        }

        model.Set(DisabledFlagName, new NbtByteTag(1));
        // The id stays a name the mod knows, so its own screen has something to
        // show when a player switches the model back on.
        var id = model.GetString("model_id");
        if (string.IsNullOrEmpty(id) || string.Equals(id, DisabledFlagName, StringComparison.Ordinal))
        {
            model.Set("model_id", new NbtStringTag(DefaultModelId));
            model.Set("select_texture", new NbtStringTag(DefaultModelId));
        }
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
