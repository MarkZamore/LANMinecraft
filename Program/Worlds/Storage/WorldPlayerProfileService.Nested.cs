using System.IO;

namespace Minecraft;

public sealed partial class WorldPlayerProfileService
{
    private static NbtCompoundTag RequireNestedWorldData(NbtFile level)
    {
        var data = level.Root.GetCompound("Data");
        if (data is null || data.Tags.Any(tag => tag.Name == "Player"))
            throw new InvalidDataException("level.dat does not contain the expected 26.1+ player storage layout.");
        return data;
    }

    private static Guid? ReadNestedHostUuid(NbtCompoundTag data)
    {
        var tags = data.Tags.Where(tag => tag.Name == WorldPlayerStorage.SingleplayerUuidTag).ToArray();
        if (tags.Length == 0) return null;

        var uuid = tags[0].Tag switch
        {
            NbtIntArrayTag array => array.ToUuid(),
            NbtStringTag text when Guid.TryParse(text.Value, out var parsed) => parsed,
            _ => null
        };
        if (tags.Length != 1 || uuid is null || uuid == Guid.Empty)
            throw new InvalidDataException("level.dat contains an invalid singleplayer_uuid tag.");
        return uuid;
    }

    private static bool HasNestedPlayerProfile(string worldPath, Guid uuid)
    {
        var primary = Path.Combine(worldPath, "players", "data", $"{uuid:D}.dat");
        var backup = primary + "_old";
        var primaryExists = File.Exists(primary);
        var backupExists = File.Exists(backup);
        if (primaryExists) ValidatePlayerDataFile(primary, uuid);
        if (backupExists) ValidatePlayerDataFile(backup, uuid);
        return primaryExists || backupExists;
    }

    private void WriteNestedHostProfile(NbtFile level, string levelPath, Guid? playerUuid, ProfileFileTransaction transaction)
    {
        var data = RequireNestedWorldData(level);
        if (ReadNestedHostUuid(data) == playerUuid) return;

        if (playerUuid is { } uuid)
            data.Set(WorldPlayerStorage.SingleplayerUuidTag, NbtIntArrayTag.FromUuid(uuid));
        else
            data.Remove(WorldPlayerStorage.SingleplayerUuidTag);
        WriteLevel(level, levelPath, transaction);
    }
}
