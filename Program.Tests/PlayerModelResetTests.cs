using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Yes Steve Model keeps a player's chosen model on the player, as a NeoForge
/// attachment in playerdata and in level.dat's Player compound. When the pack
/// asks, every player of a world goes back to the ordinary Minecraft player -
/// once - and a model picked afterwards is the player's to keep. The switch is
/// a boolean beside the id, not a value of the id: "default" is the name of one
/// of the mod's own models and gave every player that model, and "disabled" is
/// no model's name at all, so the mod fell back to its own and saved it over us
/// on the next join.
/// </summary>
public sealed class PlayerModelResetTests
{
    [Fact]
    public void ThePackAsksOnce_AndEveryPlayerIsSteveAgain()
    {
        var (pack, world, root) = NewPair(token: "2026-08-18 back to Steve");
        try
        {
            var playerData = Path.Combine(world, "playerdata");
            Directory.CreateDirectory(playerData);
            WritePlayer(Path.Combine(playerData, "aaaa.dat"), "wine_fox/15_kluonoa");
            WritePlayer(Path.Combine(playerData, "bbbb.dat"), null);
            WriteLevel(Path.Combine(world, "level.dat"), "wine_fox/17_mini");
            var service = new PlayerModelResetService();

            Assert.True(PlayerModelResetService.NeedsApplying(pack, world));
            Assert.Equal(3, service.Apply(pack, world));

            Assert.True(IsOff(NbtFile.Read(Path.Combine(playerData, "aaaa.dat")).Root));
            // A player who never opened the mod has no attachment at all, and
            // would be given the mod's own model on their first join; the switch
            // is written for them too.
            Assert.True(IsOff(NbtFile.Read(Path.Combine(playerData, "bbbb.dat")).Root));
            Assert.True(IsOff(NbtFile.Read(Path.Combine(world, "level.dat")).Root.GetCompound("Data")!.GetCompound("Player")!));

            // The player switches the mod back on and picks a fox again; the
            // pack does not undo it.
            WritePlayer(Path.Combine(playerData, "aaaa.dat"), "wine_fox/15_kluonoa");
            Assert.False(PlayerModelResetService.NeedsApplying(pack, world));
            Assert.Equal(0, service.Apply(pack, world));
            Assert.Equal("wine_fox/15_kluonoa", ModelOf(NbtFile.Read(Path.Combine(playerData, "aaaa.dat")).Root));
            Assert.False(IsOff(NbtFile.Read(Path.Combine(playerData, "aaaa.dat")).Root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Only the model changes; the rest of the player is untouched.</summary>
    [Fact]
    public void TheResetTouchesTheModelAndNothingElse()
    {
        var player = NewPlayer("wine_fox/15_kluonoa");
        player.Set("Health", new NbtFloatTag(17.5f));

        Assert.True(PlayerModelResetService.ResetModel(player));

        Assert.True(IsOff(player));
        // The id is left alone: the mod's own screen still has a model to show
        // if the player switches it back on.
        Assert.Equal("wine_fox/15_kluonoa", ModelOf(player));
        Assert.Equal(17.5f, ((NbtFloatTag)player.Tags.Single(tag => tag.Name == "Health").Tag).Value);
        Assert.NotNull(player.GetCompound(PlayerModelResetService.AttachmentsName)!.GetCompound("cataclysm:hook_falling"));
        Assert.False(PlayerModelResetService.ResetModel(player), "already the plain player: nothing to do");
    }

    private static bool IsOff(NbtCompoundTag player) =>
        player.GetCompound(PlayerModelResetService.AttachmentsName)?
            .GetCompound(PlayerModelResetService.ModelAttachmentName)?
            .GetByte(PlayerModelResetService.DisabledFlagName) == 1;

    private static string? ModelOf(NbtCompoundTag player) =>
        player.GetCompound(PlayerModelResetService.AttachmentsName)?
            .GetCompound(PlayerModelResetService.ModelAttachmentName)?.GetString("model_id");

    private static NbtCompoundTag NewPlayer(string? modelId)
    {
        var attachments = new NbtCompoundTag();
        attachments.Set("cataclysm:hook_falling", new NbtCompoundTag());
        if (modelId is not null)
        {
            var model = new NbtCompoundTag();
            model.Set("model_id", new NbtStringTag(modelId));
            model.Set("select_texture", new NbtStringTag("texture"));
            attachments.Set(PlayerModelResetService.ModelAttachmentName, model);
        }
        var player = new NbtCompoundTag();
        player.Set(PlayerModelResetService.AttachmentsName, attachments);
        return player;
    }

    private static void WritePlayer(string path, string? modelId) =>
        new NbtFile("", NewPlayer(modelId)).Write(path);

    private static void WriteLevel(string path, string modelId)
    {
        var data = new NbtCompoundTag();
        data.Set("LevelName", new NbtStringTag("Chebupeli"));
        data.Set("Player", NewPlayer(modelId));
        var root = new NbtCompoundTag();
        root.Set("Data", data);
        new NbtFile("", root).Write(path);
    }

    private static (string Pack, string World, string Root) NewPair(string token)
    {
        var root = Path.Combine(Path.GetTempPath(), "ll8-model-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "pack");
        var world = Path.Combine(root, "world");
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(pack, "launcher", PlayerModelResetService.TokenFileName), token);
        return (pack, world, root);
    }
}
