using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

public sealed partial class WorldPlayerProfileService
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private void ValidateLegacyWorld(string worldPath, LocalIdentityContext identity)
    {
        if (!identity.SteamId64.IsValid || GetCanonicalIdentityUuid(identity) != LegacyPlayerIdentity.PortableUuid(identity.SteamId64))
            throw new InvalidDataException("A bound portable Steam identity is required for a legacy world.");
        var metadata = new WorldMetadataService().Read(worldPath);
        if (!string.IsNullOrEmpty(metadata?.LegacyHostSteamId64) && !SteamId64.TryParse(metadata.LegacyHostSteamId64, out _))
            throw new InvalidDataException("Legacy world has an invalid recorded host account.");
        var path = LegacyProfilePath(worldPath, LegacyPlayerIdentity.NativeName(identity.SteamId64));
        if (File.Exists(path)) _ = NbtFile.Read(path);
        var manifest = _manifests.Read(worldPath);
        if (manifest is not null && manifest.PlayerStorage != WorldPlayerStorage.Legacy)
            throw new InvalidDataException("A legacy world contains a modern player manifest.");
    }

    private void PrepareLegacyWorld(string worldPath, LocalIdentityContext identity, ProfileFileTransaction transaction)
    {
        RecordLegacyStorage(worldPath, transaction);
        var levelPath = GetLevelPath(worldPath);
        var level = NbtFile.Read(levelPath);
        var data = level.Root.GetCompound("Data");
        var currentPlayer = data?.GetCompound("Player");
        if (currentPlayer is not null) ExportLegacyHost(worldPath, currentPlayer, transaction);
        var profile = LegacyProfilePath(worldPath, LegacyPlayerIdentity.NativeName(identity.SteamId64));
        if (File.Exists(profile))
        {
            data ??= EnsureDataCompound(level);
            data.Set("Player", NbtFile.Read(profile).Root.Clone());
            WriteLevel(level, levelPath, transaction);
        }
        else if (data?.Remove("Player") == true)
        {
            WriteLevel(level, levelPath, transaction);
        }
        SetLegacyHost(worldPath, File.Exists(profile) ? identity.SteamId64.ToString() : string.Empty, transaction);
        WriteManifest(worldPath, identity, transaction);
    }

    private string? PrepareLegacyOutgoing(string worldPath, LocalIdentityContext identity)
    {
        using var transaction = ProfileFileTransaction.Begin(_paths, "Prepare legacy world transfer");
        try
        {
            RecordLegacyStorage(worldPath, transaction);
            var path = GetLevelPath(worldPath);
            var level = NbtFile.Read(path);
            var data = level.Root.GetCompound("Data");
            var player = data?.GetCompound("Player");
            var exported = player is null ? null : ExportLegacyHost(worldPath, player, transaction);
            if (data?.Remove("Player") == true) WriteLevel(level, path, transaction);
            SetLegacyHost(worldPath, string.Empty, transaction);
            WriteManifest(worldPath, identity, transaction);
            transaction.Commit();
            return exported;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private string ExportLegacyHost(string worldPath, NbtCompoundTag player, ProfileFileTransaction transaction)
    {
        var previous = _manifests.Read(worldPath);
        var metadata = new WorldMetadataService().Read(worldPath);
        var holder = !string.IsNullOrEmpty(metadata?.LegacyHostSteamId64) ? metadata.LegacyHostSteamId64
            : previous?.PlayerStorage == WorldPlayerStorage.Legacy ? previous.CurrentHolderSteamId64 : null;
        string destination;
        if (SteamId64.TryParse(holder, out var steamId))
        {
            destination = LegacyProfilePath(worldPath, LegacyPlayerIdentity.NativeName(steamId));
        }
        else
        {
            // Imported pre-port saves have no UUID or authenticated name in Data.Player.
            // Keep that character without claiming it for the account currently launching.
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GetLevelPath(worldPath)))).ToLowerInvariant();
            destination = LegacyProfilePath(worldPath, "unassigned-" + digest);
            _logger?.Warn("An unassigned legacy host character was preserved; it was not attributed to the local player.");
        }
        transaction.Track(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        new NbtFile(string.Empty, player.Clone()).Write(destination);
        _ = NbtFile.Read(destination);
        return destination;
    }

    private static string LegacyProfilePath(string worldPath, string name) => Path.Combine(worldPath, "players", name + ".dat");

    private static void SetLegacyHost(string worldPath, string steamId, ProfileFileTransaction transaction)
    {
        var metadata = new WorldMetadataService().Read(worldPath)
            ?? throw new InvalidDataException("Legacy world storage metadata is missing.");
        metadata.LegacyHostSteamId64 = steamId;
        var path = Path.Combine(worldPath, WorldMetadataService.MetadataFileName);
        transaction.Track(path);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(metadata, LegacyJsonOptions));
    }

    private static void RecordLegacyStorage(string worldPath, ProfileFileTransaction transaction)
    {
        var service = new WorldMetadataService();
        var metadata = service.Read(worldPath) ?? new WorldMetadata { WorldId = Guid.NewGuid().ToString("D") };
        metadata.MinecraftVersion = "1.6.4";
        metadata.PlayerStorage = WorldPlayerStorage.Legacy;
        metadata.SchemaVersion = PortableFormat.SchemaVersion;
        var path = Path.Combine(worldPath, WorldMetadataService.MetadataFileName);
        transaction.Track(path);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(metadata, LegacyJsonOptions));
    }
}
