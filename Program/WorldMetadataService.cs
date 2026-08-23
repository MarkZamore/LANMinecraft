using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldMetadataService
{
    public const string MetadataFileName = ".minecraft-portable-world.json";
    /// <summary>The launcher's one format version; see <see cref="PortableFormat"/>.</summary>
    public const int CurrentSchemaVersion = PortableFormat.SchemaVersion;
    private const string UnknownBuildName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private const string UnknownOwnerName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WorldMetadata? Read(string worldPath)
    {
        var metadataPath = GetMetadataPath(worldPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public WorldMetadata? EnsureMetadata(string worldPath, WorldMetadataContext? context)
    {
        lock (_gate) return EnsureMetadataCore(worldPath, context);
    }

    private WorldMetadata? EnsureMetadataCore(string worldPath, WorldMetadataContext? context)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var existing = Read(worldPath);
        if (existing is not null)
        {
            if (EnsureWorldId(existing))
            {
                try
                {
                    AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(existing, _jsonOptions));
                }
                catch
                {
                    return null;
                }
            }
            return existing;
        }
        if (File.Exists(metadataPath) || context is null)
        {
            return null;
        }

        var metadata = new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"),
            BuildName = string.IsNullOrWhiteSpace(context.BuildName) ? UnknownBuildName : context.BuildName,
            BuildRelativePath = context.BuildRelativePath,
            PackHash = context.PackHash,
            OwnerIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            OwnerIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            CurrentHolderIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            CurrentHolderIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            SchemaVersion = CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MarkedBy = "LANMinecraft.exe"
        };

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renames the build a world belongs to, and only for a world still naming
    /// <paramref name="legacyBuildRelativePath"/>. The world id, its pack hash
    /// and everyone recorded around it stay exactly as they are.
    /// </summary>
    public bool TryRebindBuild(
        string worldPath,
        string legacyBuildRelativePath,
        string buildName,
        string buildRelativePath)
    {
        lock (_gate)
        {
            var metadata = Read(worldPath);
            if (metadata is null ||
                !IsSameBuildPath(metadata.BuildRelativePath, legacyBuildRelativePath))
            {
                return false;
            }

            metadata.BuildName = buildName;
            metadata.BuildRelativePath = buildRelativePath;
            try
            {
                AtomicFile.WriteAllText(
                    GetMetadataPath(worldPath),
                    JsonSerializer.Serialize(metadata, _jsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool IsSameBuildPath(string? left, string? right) =>
        string.Equals(
            left?.Trim().Trim('\\', '/'),
            right?.Trim().Trim('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a world belongs to the build a player has selected, and so should
    /// be offered to them. A world made on one pack has no business being opened
    /// on another: its blocks and entities belong to mods the other pack may not
    /// have, and opening it there is how a world loses content.
    ///
    /// Two kinds of world are shown anyway. One whose build was never recorded
    /// cannot be attributed to anyone, and hiding it would be the launcher
    /// losing somebody's world rather than protecting it. One recorded under a
    /// name the built-in pack used to have belongs to the pack that was renamed,
    /// and it is offered under the new name until the migration rewrites it.
    /// </summary>
    public static bool BelongsToBuild(string? recordedBuildRelativePath, string? selectedBuildRelativePath)
    {
        var recorded = recordedBuildRelativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(recorded)) return true;

        var selected = selectedBuildRelativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(selected)) return true;

        if (string.Equals(recorded, selected, StringComparison.OrdinalIgnoreCase)) return true;

        return string.Equals(
                   selected,
                   PortablePackSyncService.DefaultPackRelativePath,
                   StringComparison.OrdinalIgnoreCase) &&
               LegacyPackMigrationService.IsLegacyPack(recorded);
    }

    public string GetBuildName(string worldPath)
    {
        var metadata = Read(worldPath);
        return string.IsNullOrWhiteSpace(metadata?.BuildName) ? UnknownBuildName : metadata.BuildName;
    }

    /// <summary>
    /// A Steam id only ever reaches the document as a plain SteamID64; anything
    /// else (including the empty string a machine without Steam produces) is
    /// treated as "not known".
    /// </summary>
    private static string NormalizeSteamId(string? value) =>
        SteamId64.TryNormalize(value, out var canonical) ? canonical : string.Empty;

    public bool TryWriteOwnerMetadata(
        string worldPath,
        string? ownerId,
        string? ownerName,
        bool overwriteExistingOwner = false,
        string? ownerSteamId64 = null)
    {
        lock (_gate)
        {
            return TryWriteOwnerMetadataCore(
                worldPath, ownerId, ownerName, overwriteExistingOwner, ownerSteamId64);
        }
    }

    private bool TryWriteOwnerMetadataCore(
        string worldPath,
        string? ownerId,
        string? ownerName,
        bool overwriteExistingOwner,
        string? ownerSteamId64)
    {
        var metadataPath = GetMetadataPath(worldPath);
        WorldMetadata metadata;
        if (File.Exists(metadataPath))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions) ?? new WorldMetadata();
            }
            catch
            {
                return false;
            }
        }
        else
        {
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                BuildRelativePath = string.Empty,
                PackHash = string.Empty,
                SchemaVersion = CurrentSchemaVersion,
                MarkedBy = "LANMinecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var normalizedOwnerId = string.IsNullOrWhiteSpace(ownerId) ? string.Empty : ownerId.Trim();
        var normalizedOwnerName = string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim();
        var normalizedOwnerSteamId = NormalizeSteamId(ownerSteamId64);

        if (!overwriteExistingOwner &&
            !string.IsNullOrWhiteSpace(metadata.OwnerIdentityId))
        {
            var ownerIsLocalPlayer = string.Equals(
                metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase);
            var changed = false;

            // The name is refreshed only from the owner's own machine.
            if (ownerIsLocalPlayer &&
                !string.IsNullOrWhiteSpace(normalizedOwnerName) &&
                !string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal))
            {
                metadata.OwnerIdentityName = normalizedOwnerName;
                changed = true;
            }

            // The owner UUID stays exactly as it is; only the Steam account
            // behind it is learned - from the owner's own machine, or from the
            // table of players who predate Steam, which any machine knows.
            if (string.IsNullOrWhiteSpace(metadata.OwnerSteamId64))
            {
                var learnedSteamId = ownerIsLocalPlayer && normalizedOwnerSteamId.Length != 0
                    ? normalizedOwnerSteamId
                    : KnownSteamPlayers.TryGetSteamId(metadata.OwnerIdentityId, out var knownOwner)
                        ? knownOwner.ToString()
                        : string.Empty;
                if (learnedSteamId.Length != 0)
                {
                    metadata.OwnerSteamId64 = learnedSteamId;
                    if (metadata.SchemaVersion < CurrentSchemaVersion)
                    {
                        metadata.SchemaVersion = CurrentSchemaVersion;
                    }
                    changed = true;
                }
            }

            if (changed)
            {
                try
                {
                    AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        if (string.Equals(metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal) &&
            string.Equals(metadata.OwnerSteamId64, normalizedOwnerSteamId, StringComparison.Ordinal))
        {
            return true;
        }

        metadata.OwnerIdentityId = normalizedOwnerId;
        metadata.OwnerIdentityName = normalizedOwnerName;
        if (normalizedOwnerSteamId.Length != 0) metadata.OwnerSteamId64 = normalizedOwnerSteamId;
        EnsureWorldId(metadata);
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
        }

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryWriteCurrentHolderMetadata(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred,
        string? holderSteamId64 = null)
    {
        lock (_gate)
        {
            return TryWriteCurrentHolderMetadataCore(
                worldPath, holderId, holderName, transferred, holderSteamId64);
        }
    }

    private bool TryWriteCurrentHolderMetadataCore(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred,
        string? holderSteamId64)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var metadata = Read(worldPath);
        if (metadata is null)
        {
            if (File.Exists(metadataPath)) return false;
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                MarkedBy = "LANMinecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        // Never downgrade: a document written by a newer build keeps its version.
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
        }
        EnsureWorldId(metadata);
        var normalizedHolderId = string.IsNullOrWhiteSpace(holderId) ? string.Empty : holderId.Trim();
        var normalizedHolderName = string.IsNullOrWhiteSpace(holderName) ? UnknownOwnerName : holderName.Trim();
        var normalizedHolderSteamId = NormalizeSteamId(holderSteamId64) is { Length: > 0 } holderSteamId
            ? holderSteamId
            : KnownSteamPlayers.TryGetSteamId(normalizedHolderId, out var knownHolder)
                ? knownHolder.ToString()
                : string.Empty;

        // The world list refreshes every two seconds; without this, each refresh
        // rewrote a JSON file per world for nothing.
        if (!transferred &&
            metadata.SchemaVersion == CurrentSchemaVersion &&
            string.Equals(metadata.CurrentHolderIdentityId, normalizedHolderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.CurrentHolderIdentityName, normalizedHolderName, StringComparison.Ordinal) &&
            string.Equals(metadata.CurrentHolderSteamId64, normalizedHolderSteamId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(metadata.WorldId))
        {
            return true;
        }

        metadata.CurrentHolderIdentityId = normalizedHolderId;
        metadata.CurrentHolderIdentityName = normalizedHolderName;
        metadata.CurrentHolderSteamId64 = normalizedHolderSteamId;
        if (transferred) metadata.LastSuccessfulTransferUtc = DateTimeOffset.UtcNow;
        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMetadataPath(string worldPath)
    {
        return Path.Combine(worldPath, MetadataFileName);
    }

    public string EnsureWorldId(string worldPath, WorldMetadataContext? context = null)
    {
        lock (_gate)
        {
            var metadata = EnsureMetadataCore(worldPath, context)
                ?? throw new InvalidDataException($"World metadata is missing or damaged: {Path.GetFileName(worldPath)}");
            if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
            {
                throw new InvalidDataException($"World metadata has an invalid WorldId: {Path.GetFileName(worldPath)}");
            }
            return worldId.ToString("D");
        }
    }

    private static bool EnsureWorldId(WorldMetadata metadata)
    {
        var changed = false;
        if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
        {
            metadata.WorldId = Guid.NewGuid().ToString("D");
            changed = true;
        }
        else
        {
            var normalized = worldId.ToString("D");
            if (!string.Equals(metadata.WorldId, normalized, StringComparison.Ordinal))
            {
                metadata.WorldId = normalized;
                changed = true;
            }
        }
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }
        return changed;
    }
}
